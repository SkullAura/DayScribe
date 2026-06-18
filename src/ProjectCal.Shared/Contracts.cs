namespace ProjectCal.Shared;

public enum AttachmentType
{
    Photo = 0,
    Audio = 1
}

public enum UploadStatus
{
    LocalOnly = 0,
    Uploading = 1,
    Uploaded = 2,
    Failed = 3
}

public enum TranscriptStatus
{
    None = 0,
    Pending = 1,
    Processing = 2,
    Done = 3,
    Failed = 4
}

public enum SyncOperation
{
    Upsert = 0,
    Delete = 1
}

public sealed record RegisterRequest(string Email, string Password);

public sealed record RegisterResponse(Guid UserId, string Email, bool EmailConfirmed, string? DevelopmentEmailToken);

public sealed record ConfirmEmailRequest(string Email, string Token);

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, UserProfileDto User);

public sealed record RefreshTokenRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ForgotPasswordResponse(string Message, string? DevelopmentResetToken);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record UserProfileDto(Guid Id, string Email, bool EmailConfirmed);

public sealed record NoteDto(
    Guid Id,
    string Title,
    string Body,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly? EndTime,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    long SyncVersion,
    IReadOnlyList<AttachmentDto> Attachments,
    TranscriptDto? Transcript);

public sealed record UpsertNoteRequest(
    Guid? Id,
    string Title,
    string Body,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly? EndTime,
    long SyncVersion,
    string? TranscriptText = null,
    TranscriptStatus? TranscriptStatus = null,
    string? TranscriptLanguage = null);

public sealed record UpsertTranscriptRequest(
    string Language,
    string Text,
    TranscriptStatus Status);

public sealed record RenameAttachmentRequest(string FileName);

public sealed record AttachmentDto(
    Guid Id,
    Guid NoteId,
    AttachmentType Type,
    string FileName,
    string MimeType,
    long Size,
    UploadStatus UploadStatus,
    DateTimeOffset CreatedAt);

public sealed record TranscriptDto(
    Guid Id,
    Guid NoteId,
    Guid AttachmentId,
    string Language,
    string? Text,
    TranscriptStatus Status,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record SyncNoteMutation(SyncOperation Operation, UpsertNoteRequest Note);

public sealed record SyncRequest(DateTimeOffset? LastSyncAt, IReadOnlyList<SyncNoteMutation> Notes);

public sealed record SyncResponse(
    DateTimeOffset ServerTime,
    IReadOnlyList<NoteDto> Notes,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<TranscriptDto> Transcripts);

public sealed record QueueStatsDto(int PendingTranscriptions, int FailedTranscriptions);

public sealed record AdminStatsDto(int Users, int Notes, int Attachments, QueueStatsDto Queue);
