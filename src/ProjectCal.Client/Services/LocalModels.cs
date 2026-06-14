using ProjectCal.Shared;

namespace ProjectCal_Client.Services;

public sealed class LocalNote
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long SyncVersion { get; set; }
    public bool IsDirty { get; set; }
    public string? TranscriptText { get; set; }
    public TranscriptStatus TranscriptStatus { get; set; }
    public string HourLabel => StartTime.ToString("HH:mm");
    public string Subtitle => $"{Date:yyyy-MM-dd} at {StartTime:HH:mm} | sync v{SyncVersion} | {TranscriptStatus}";
}

public sealed class LocalAttachment
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public AttachmentType Type { get; set; }
    public string LocalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public bool IsUploaded { get; set; }
}

public sealed class LocalTranscript
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Guid AttachmentId { get; set; }
    public string Language { get; set; } = "auto";
    public string? Text { get; set; }
    public TranscriptStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LocalAttachmentSummary
{
    public int PhotoCount { get; set; }
    public int AudioCount { get; set; }
}
