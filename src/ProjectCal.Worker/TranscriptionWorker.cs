using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectCal.Api.Data;
using ProjectCal.Shared;

namespace ProjectCal.Worker;

public sealed class TranscriptionWorker(
    IServiceScopeFactory scopeFactory,
    ISpeechToTextService speechToText,
    ILogger<TranscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transcription worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessOneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.Transcripts
            .Include(x => x.Attachment)
            .Where(x => x.Status == TranscriptStatus.Pending || (x.Status == TranscriptStatus.Failed && x.Attempts < 3))
            .ToArrayAsync(cancellationToken);
        var transcript = candidates.OrderBy(x => x.UpdatedAt).FirstOrDefault();

        if (transcript?.Attachment is null)
        {
            return;
        }

        transcript.Status = TranscriptStatus.Processing;
        transcript.ErrorMessage = null;
        transcript.Attempts++;
        transcript.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            transcript.Text = await speechToText.TranscribeAsync(transcript.Attachment.StoredPath, transcript.Language, cancellationToken);
            transcript.Status = TranscriptStatus.Done;
            transcript.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            transcript.Status = TranscriptStatus.Failed;
            transcript.ErrorMessage = ex.Message;
            logger.LogWarning(ex, "Transcription failed for attachment {AttachmentId}.", transcript.AttachmentId);
        }

        transcript.UpdatedAt = DateTimeOffset.UtcNow;

        var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == transcript.NoteId, cancellationToken);
        if (note is not null)
        {
            note.UpdatedAt = transcript.UpdatedAt;
            note.SyncVersion++;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

public interface ISpeechToTextService
{
    Task<string> TranscribeAsync(string storedPath, string language, CancellationToken cancellationToken);
}

public sealed class SpeechToTextService(HttpClient httpClient, IConfiguration configuration) : ISpeechToTextService
{
    public async Task<string> TranscribeAsync(string storedPath, string language, CancellationToken cancellationToken)
    {
        var audioPath = ResolveAudioPath(storedPath);
        if (TryResolveLocalWhisper(out var executablePath, out var modelPath) && IsWhisperSupportedAudio(audioPath))
        {
            return await TranscribeWithLocalWhisperAsync(audioPath, language, executablePath, modelPath, cancellationToken);
        }

        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return await TranscribeWithOpenAIAsync(audioPath, language, apiKey, cancellationToken);
        }

        var endpoint = configuration["Transcription:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return configuration["Transcription:StubText"]
                ?? $"Audio {storedPath} queued with language '{language}'. Configure a real STT endpoint for production.";
        }

        var response = await httpClient.PostAsJsonAsync(endpoint, new { storedPath, language }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TranscriptionResponse>(cancellationToken);
        return payload?.Text ?? "";
    }

    private async Task<string> TranscribeWithLocalWhisperAsync(
        string audioPath,
        string language,
        string executablePath,
        string modelPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        var outputBase = Path.Combine(Path.GetTempPath(), "ProjectCal", "whisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
        var outputTextPath = outputBase + ".txt";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(modelPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(audioPath);
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add(NormalizeWhisperLanguage(language));
        process.StartInfo.ArgumentList.Add("-otxt");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add(outputBase);
        process.StartInfo.ArgumentList.Add("-nt");
        process.StartInfo.ArgumentList.Add("-np");

        process.Start();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Local whisper.cpp failed with exit code {process.ExitCode}. {standardError}");
        }

        var text = File.Exists(outputTextPath)
            ? await File.ReadAllTextAsync(outputTextPath, cancellationToken)
            : standardOutput;

        TryDelete(outputTextPath);
        return text.Trim();
    }

    private async Task<string> TranscribeWithOpenAIAsync(string audioPath, string language, string apiKey, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(audioPath);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(audioPath));
        form.Add(file, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(configuration["OpenAI:TranscriptionModel"] ?? "gpt-4o-mini-transcribe"), "model");
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    private string ResolveAudioPath(string storedPath)
    {
        if (Path.IsPathRooted(storedPath))
        {
            return storedPath;
        }

        var normalized = storedPath.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>();

        var configuredRoot = configuration["Storage:RootPath"];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            candidates.Add(Path.Combine(configuredRoot, normalized));
        }

        candidates.Add(Path.GetFullPath(normalized, Directory.GetCurrentDirectory()));
        candidates.Add(Path.GetFullPath(normalized, AppContext.BaseDirectory));

        var apiMediaRoot = FindUpwards("src", "ProjectCal.Api", "App_Data", "media");
        if (apiMediaRoot is not null)
        {
            candidates.Add(Path.Combine(apiMediaRoot, normalized));
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates.First();
    }

    private static string? FindUpwards(params string[] pathParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".webm" => "audio/webm",
            ".mpeg" => "audio/mpeg",
            ".mpga" => "audio/mpeg",
            _ => "application/octet-stream"
        };
    }

    private bool TryResolveLocalWhisper(out string executablePath, out string modelPath)
    {
        executablePath = "";
        modelPath = "";

        var configuredExecutable = configuration["Transcription:WhisperExecutablePath"];
        var configuredModel = configuration["Transcription:WhisperModelPath"];
        var executableCandidates = new[]
        {
            configuredExecutable,
            Path.Combine(AppContext.BaseDirectory, "Tools", "whisper", "win-x64", "whisper-cli.exe"),
            FindUpwards("src", "ProjectCal.Worker", "Tools", "whisper", "win-x64", "whisper-cli.exe")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>();
        var modelCandidates = new[]
        {
            configuredModel,
            Path.Combine(AppContext.BaseDirectory, "Models", "whisper", "ggml-tiny.bin"),
            FindUpwards("src", "ProjectCal.Worker", "Models", "whisper", "ggml-tiny.bin")
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>();

        executablePath = executableCandidates.FirstOrDefault(File.Exists) ?? "";
        modelPath = modelCandidates.FirstOrDefault(File.Exists) ?? "";
        return !string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(modelPath);
    }

    private static bool IsWhisperSupportedAudio(string audioPath)
    {
        return Path.GetExtension(audioPath).ToLowerInvariant() is ".flac" or ".mp3" or ".ogg" or ".wav";
    }

    private static string NormalizeWhisperLanguage(string language)
    {
        return string.IsNullOrWhiteSpace(language) ? "auto" : language.ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record TranscriptionResponse(string Text);
}
