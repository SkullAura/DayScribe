using ProjectCal.Api.Data.Entities;
using ProjectCal.Shared;

namespace ProjectCal.Api;

public static class Mapping
{
    public static NoteDto ToDto(this NoteEntity note)
    {
        return new NoteDto(
            note.Id,
            note.Title,
            note.Body,
            note.Date,
            note.StartTime,
            note.EndTime,
            note.CreatedAt,
            note.UpdatedAt,
            note.DeletedAt,
            note.SyncVersion,
            note.Attachments.OrderBy(x => x.CreatedAt).Select(x => x.ToDto()).ToArray(),
            note.Transcripts.OrderByDescending(x => x.UpdatedAt).FirstOrDefault()?.ToDto());
    }

    public static AttachmentDto ToDto(this AttachmentEntity attachment)
    {
        return new AttachmentDto(
            attachment.Id,
            attachment.NoteId,
            attachment.Type,
            attachment.FileName,
            attachment.MimeType,
            attachment.Size,
            attachment.UploadStatus,
            attachment.CreatedAt);
    }

    public static TranscriptDto ToDto(this TranscriptEntity transcript)
    {
        return new TranscriptDto(
            transcript.Id,
            transcript.NoteId,
            transcript.AttachmentId,
            transcript.Language,
            transcript.Text,
            transcript.Status,
            transcript.ErrorMessage,
            transcript.UpdatedAt);
    }
}
