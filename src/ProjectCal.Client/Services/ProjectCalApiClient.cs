using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ProjectCal.Shared;
using Windows.Storage;

namespace ProjectCal_Client.Services;

public sealed class ProjectCalApiClient
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5009") };
    private string? _accessToken;
    private string? _refreshToken;

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_accessToken);
    public UserProfileDto? CurrentUser { get; private set; }

    public async Task<RegisterResponse> RegisterAsync(string email, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    public async Task ConfirmEmailAsync(string email, string token)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmailRequest(email, token));
        await EnsureSuccessAsync(response);
    }

    public async Task LoginAsync(string email, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        await EnsureSuccessAsync(response);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        ApplyAuth(auth);
        SaveRefreshToken(auth.RefreshToken);
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        var savedRefreshToken = ApplicationData.Current.LocalSettings.Values["refresh_token"] as string;
        if (string.IsNullOrWhiteSpace(savedRefreshToken))
        {
            return false;
        }

        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(savedRefreshToken));
            await EnsureSuccessAsync(response);
            var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
            ApplyAuth(auth);
            SaveRefreshToken(auth.RefreshToken);
            return true;
        }
        catch
        {
            Logout();
            return false;
        }
    }

    public void Logout()
    {
        _accessToken = null;
        _refreshToken = null;
        CurrentUser = null;
        _http.DefaultRequestHeaders.Authorization = null;
        ApplicationData.Current.LocalSettings.Values.Remove("refresh_token");
    }

    public async Task<string?> ForgotPasswordAsync(string email)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));
        await EnsureSuccessAsync(response);
        var payload = await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>();
        return payload?.DevelopmentResetToken;
    }

    public async Task ResetPasswordAsync(string email, string token, string newPassword)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(email, token, newPassword));
        await EnsureSuccessAsync(response);
    }

    public async Task<SyncResponse> SyncAsync(SyncRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/sync", request);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<SyncResponse>())!;
    }

    public async Task UploadAttachmentAsync(LocalAttachment attachment, string language)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(attachment.LocalPath);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(attachment.MimeType);
        form.Add(file, "file", attachment.FileName);

        var url = $"/api/notes/{attachment.NoteId}/attachments?type={attachment.Type}&language={Uri.EscapeDataString(language)}";
        var response = await _http.PostAsync(url, form);
        await EnsureSuccessAsync(response);
    }

    private void ApplyAuth(AuthResponse auth)
    {
        _accessToken = auth.AccessToken;
        _refreshToken = auth.RefreshToken;
        CurrentUser = auth.User;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private static void SaveRefreshToken(string refreshToken)
    {
        ApplicationData.Current.LocalSettings.Values["refresh_token"] = refreshToken;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var message = TryReadError(body) ?? FriendlyStatusMessage(response.StatusCode);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string? TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }

            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return null;
    }

    private static string FriendlyStatusMessage(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Check the entered data and try again.",
            System.Net.HttpStatusCode.Conflict => "This email is already registered. Login or reset the password.",
            System.Net.HttpStatusCode.Unauthorized => "Email or password is incorrect.",
            _ => $"Request failed: {(int)statusCode} {statusCode}."
        };
    }
}
