using ProjectCal.Shared;

namespace ProjectCal.Api.Data.Entities;

public sealed class UserEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool EmailConfirmed { get; set; }
    public string? EmailConfirmationTokenHash { get; set; }
    public DateTimeOffset? EmailConfirmationExpiresAt { get; set; }
    public string? PasswordResetTokenHash { get; set; }
    public DateTimeOffset? PasswordResetExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RefreshTokenEntity> RefreshTokens { get; set; } = [];
    public List<NoteEntity> Notes { get; set; } = [];
}

public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public UserEntity? User { get; set; }
}

public sealed class NoteEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public long SyncVersion { get; set; }
    public UserEntity? User { get; set; }
    public List<AttachmentEntity> Attachments { get; set; } = [];
    public List<TranscriptEntity> Transcripts { get; set; } = [];
}

public sealed class AttachmentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid NoteId { get; set; }
    public AttachmentType Type { get; set; }
    public string FileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public UploadStatus UploadStatus { get; set; } = UploadStatus.Uploaded;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public NoteEntity? Note { get; set; }
}

public sealed class TranscriptEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid NoteId { get; set; }
    public Guid AttachmentId { get; set; }
    public string Language { get; set; } = "auto";
    public string? Text { get; set; }
    public TranscriptStatus Status { get; set; } = TranscriptStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public NoteEntity? Note { get; set; }
    public AttachmentEntity? Attachment { get; set; }
}
