using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using ProjectCal.Api.Data.Entities;

namespace ProjectCal.Api.Services;

public static class PasswordService
{
    private const int Iterations = 100_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}

public sealed class TokenService(IConfiguration configuration)
{
    public (string AccessToken, DateTimeOffset ExpiresAt) CreateAccessToken(UserEntity user)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(configuration.GetValue("Jwt:AccessTokenMinutes", 30));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GetSigningKey(configuration)));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("email_confirmed", user.EmailConfirmed.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "ProjectCal",
            audience: configuration["Jwt:Audience"] ?? "ProjectCal.Client",
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static string GetSigningKey(IConfiguration configuration)
    {
        var key = configuration["Jwt:SigningKey"];
        return string.IsNullOrWhiteSpace(key)
            ? "dev-only-change-this-signing-key-32-bytes"
            : key;
    }
}

public interface IFileStorage
{
    Task<string> SaveAsync(Guid userId, Guid attachmentId, IFormFile file, CancellationToken cancellationToken);
    Task<(Stream Stream, string FileName, string MimeType)?> OpenAsync(AttachmentEntity attachment, CancellationToken cancellationToken);
}

public sealed class LocalFileStorage(IWebHostEnvironment environment, IConfiguration configuration) : IFileStorage
{
    public async Task<string> SaveAsync(Guid userId, Guid attachmentId, IFormFile file, CancellationToken cancellationToken)
    {
        var root = configuration["Storage:RootPath"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(environment.ContentRootPath, "App_Data", "media");
        }

        var extension = Path.GetExtension(file.FileName);
        var relativePath = Path.Combine(userId.ToString("N"), $"{attachmentId:N}{extension}");
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var output = File.Create(fullPath);
        await file.CopyToAsync(output, cancellationToken);
        return relativePath.Replace('\\', '/');
    }

    public Task<(Stream Stream, string FileName, string MimeType)?> OpenAsync(AttachmentEntity attachment, CancellationToken cancellationToken)
    {
        var root = configuration["Storage:RootPath"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(environment.ContentRootPath, "App_Data", "media");
        }

        var fullPath = Path.Combine(root, attachment.StoredPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<(Stream, string, string)?>(null);
        }

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult<(Stream, string, string)?>((stream, attachment.FileName, attachment.MimeType));
    }
}

public interface IServerTranscriptionService
{
    Task<string> TranscribeAsync(Stream audio, string fileName, string mimeType, string language, CancellationToken cancellationToken);
}

public sealed class GroqServerTranscriptionService(IConfiguration configuration) : IServerTranscriptionService
{
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("https://api.groq.com") };

    public async Task<string> TranscribeAsync(Stream audio, string fileName, string mimeType, string language, CancellationToken cancellationToken)
    {
        var apiKey = configuration["Groq:ApiKey"] ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Server transcription is not configured. Set GROQ_API_KEY.");
        }

        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? GetMimeType(fileName) : mimeType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(configuration["Groq:TranscriptionModel"] ?? "whisper-large-v3-turbo"), "model");
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Groq transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";
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
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("Missing user id.");
    }
}
