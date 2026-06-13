using Microsoft.Data.Sqlite;
using ProjectCal.Shared;
using Windows.Storage;

namespace ProjectCal_Client.Services;

public sealed class LocalNoteStore
{
    private readonly string _dbPath;
    private readonly string _mediaRoot;

    public LocalNoteStore()
    {
        var root = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ProjectCal");
        Directory.CreateDirectory(root);
        _mediaRoot = Path.Combine(root, "media");
        Directory.CreateDirectory(_mediaRoot);
        _dbPath = Path.Combine(root, "notes.db");
    }

    public async Task InitializeAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                date TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                sync_version INTEGER NOT NULL,
                is_dirty INTEGER NOT NULL,
                transcript_text TEXT NULL,
                transcript_status INTEGER NOT NULL
            );
            """);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS attachments (
                id TEXT PRIMARY KEY,
                note_id TEXT NOT NULL,
                type INTEGER NOT NULL,
                local_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                size INTEGER NOT NULL,
                is_uploaded INTEGER NOT NULL
            );
            """);
    }

    public async Task<IReadOnlyList<LocalNote>> GetNotesAsync(DateOnly date, string? search)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                   sync_version, is_dirty, transcript_text, transcript_status
            FROM notes
            WHERE deleted_at IS NULL AND date = $date
              AND ($search IS NULL OR title LIKE $searchLike OR body LIKE $searchLike OR transcript_text LIKE $searchLike)
            ORDER BY start_time
            """;
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search);
        command.Parameters.AddWithValue("$searchLike", $"%{search}%");

        var notes = new List<LocalNote>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notes.Add(ReadNote(reader));
        }

        return notes;
    }

    public async Task<IReadOnlyList<LocalNote>> GetAllNotesAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                   sync_version, is_dirty, transcript_text, transcript_status
            FROM notes
            WHERE deleted_at IS NULL
            ORDER BY date DESC, start_time ASC
            """;

        var notes = new List<LocalNote>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notes.Add(ReadNote(reader));
        }

        return notes;
    }

    public async Task<LocalNote> UpsertNoteAsync(Guid? id, string title, string body, DateOnly date, TimeOnly startTime, TimeOnly? endTime)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new LocalNote
        {
            Id = id ?? Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled note" : title.Trim(),
            Body = body,
            Date = date,
            StartTime = startTime,
            EndTime = endTime,
            CreatedAt = now,
            UpdatedAt = now,
            SyncVersion = 0,
            IsDirty = true,
            TranscriptStatus = TranscriptStatus.None
        };

        await using var connection = Open();
        await connection.OpenAsync();
        var existing = await GetNoteAsync(connection, note.Id);
        if (existing is not null)
        {
            note.CreatedAt = existing.CreatedAt;
            note.SyncVersion = existing.SyncVersion + 1;
            note.TranscriptText = existing.TranscriptText;
            note.TranscriptStatus = existing.TranscriptStatus;
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                               sync_version, is_dirty, transcript_text, transcript_status)
            VALUES ($id, $title, $body, $date, $start, $end, $created, $updated, NULL, $version, 1, $text, $status)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                body = excluded.body,
                date = excluded.date,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                updated_at = excluded.updated_at,
                deleted_at = NULL,
                sync_version = excluded.sync_version,
                is_dirty = 1
            """;
        BindNote(command, note);
        await command.ExecuteNonQueryAsync();
        return note;
    }

    public async Task<LocalNote?> GetNoteByIdAsync(Guid id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        return await GetNoteAsync(connection, id);
    }

    public async Task DeleteNoteAsync(Guid id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET deleted_at = $deleted, updated_at = $updated, sync_version = sync_version + 1, is_dirty = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$deleted", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<LocalNote>> GetDirtyNotesAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                   sync_version, is_dirty, transcript_text, transcript_status
            FROM notes
            WHERE is_dirty = 1
            """;
        var notes = new List<LocalNote>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notes.Add(ReadNote(reader));
        }

        return notes;
    }

    public async Task ApplyServerNoteAsync(NoteDto note)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var local = new LocalNote
        {
            Id = note.Id,
            Title = note.Title,
            Body = note.Body,
            Date = note.Date,
            StartTime = note.StartTime,
            EndTime = note.EndTime,
            CreatedAt = note.CreatedAt,
            UpdatedAt = note.UpdatedAt,
            DeletedAt = note.DeletedAt,
            SyncVersion = note.SyncVersion,
            IsDirty = false,
            TranscriptText = note.Transcript?.Text,
            TranscriptStatus = note.Transcript?.Status ?? TranscriptStatus.None
        };

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                               sync_version, is_dirty, transcript_text, transcript_status)
            VALUES ($id, $title, $body, $date, $start, $end, $created, $updated, $deleted, $version, 0, $text, $status)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                body = excluded.body,
                date = excluded.date,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                updated_at = excluded.updated_at,
                deleted_at = excluded.deleted_at,
                sync_version = excluded.sync_version,
                is_dirty = 0,
                transcript_text = excluded.transcript_text,
                transcript_status = excluded.transcript_status
            """;
        BindNote(command, local);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ApplyServerTranscriptAsync(TranscriptDto transcript)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
            SET transcript_text = $text,
                transcript_status = $status,
                updated_at = $updated
            WHERE id = $note_id
            """;
        command.Parameters.AddWithValue("$note_id", transcript.NoteId.ToString());
        command.Parameters.AddWithValue("$text", transcript.Text ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)transcript.Status);
        command.Parameters.AddWithValue("$updated", transcript.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddAttachmentAsync(Guid noteId, StorageFile file, AttachmentType type)
    {
        var id = Guid.NewGuid();
        var extension = Path.GetExtension(file.Name);
        var localPath = Path.Combine(_mediaRoot, $"{id:N}{extension}");
        await file.CopyAsync(await StorageFolder.GetFolderFromPathAsync(_mediaRoot), Path.GetFileName(localPath), NameCollisionOption.ReplaceExisting);

        var properties = await file.GetBasicPropertiesAsync();
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachments (id, note_id, type, local_path, file_name, mime_type, size, is_uploaded)
            VALUES ($id, $note_id, $type, $local_path, $file_name, $mime_type, $size, 0)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$note_id", noteId.ToString());
        command.Parameters.AddWithValue("$type", (int)type);
        command.Parameters.AddWithValue("$local_path", localPath);
        command.Parameters.AddWithValue("$file_name", file.Name);
        command.Parameters.AddWithValue("$mime_type", type == AttachmentType.Audio ? AudioMimeType(file.Name) : PhotoMimeType(file.Name));
        command.Parameters.AddWithValue("$size", (long)properties.Size);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<LocalAttachment>> GetPendingAttachmentsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, note_id, type, local_path, file_name, mime_type, size, is_uploaded FROM attachments WHERE is_uploaded = 0";
        var attachments = new List<LocalAttachment>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            attachments.Add(new LocalAttachment
            {
                Id = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                Type = (AttachmentType)reader.GetInt32(2),
                LocalPath = reader.GetString(3),
                FileName = reader.GetString(4),
                MimeType = reader.GetString(5),
                Size = reader.GetInt64(6),
                IsUploaded = reader.GetInt64(7) == 1
            });
        }

        return attachments;
    }

    public async Task<IReadOnlyList<LocalAttachment>> GetAttachmentsForNoteAsync(Guid noteId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, note_id, type, local_path, file_name, mime_type, size, is_uploaded
            FROM attachments
            WHERE note_id = $note_id
            ORDER BY rowid DESC
            """;
        command.Parameters.AddWithValue("$note_id", noteId.ToString());

        var attachments = new List<LocalAttachment>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            attachments.Add(new LocalAttachment
            {
                Id = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                Type = (AttachmentType)reader.GetInt32(2),
                LocalPath = reader.GetString(3),
                FileName = reader.GetString(4),
                MimeType = reader.GetString(5),
                Size = reader.GetInt64(6),
                IsUploaded = reader.GetInt64(7) == 1
            });
        }

        return attachments;
    }

    public async Task<IReadOnlyDictionary<Guid, LocalAttachmentSummary>> GetAttachmentSummariesForNotesAsync(IEnumerable<Guid> noteIds)
    {
        var ids = noteIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, LocalAttachmentSummary>();
        }

        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        var parameterNames = ids.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText = $"""
            SELECT note_id, type, COUNT(*)
            FROM attachments
            WHERE note_id IN ({string.Join(", ", parameterNames)})
            GROUP BY note_id, type
            """;

        for (var i = 0; i < ids.Length; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i], ids[i].ToString());
        }

        var summaries = ids.ToDictionary(id => id, _ => new LocalAttachmentSummary());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var noteId = Guid.Parse(reader.GetString(0));
            var type = (AttachmentType)reader.GetInt32(1);
            var count = reader.GetInt32(2);
            if (!summaries.TryGetValue(noteId, out var summary))
            {
                summary = new LocalAttachmentSummary();
                summaries[noteId] = summary;
            }

            if (type == AttachmentType.Photo)
            {
                summary.PhotoCount = count;
            }
            else if (type == AttachmentType.Audio)
            {
                summary.AudioCount = count;
            }
        }

        return summaries;
    }

    public async Task MarkAttachmentUploadedAsync(Guid id)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE attachments SET is_uploaded = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> ApplyServerAttachmentAsync(AttachmentDto attachment, byte[] content)
    {
        await using var connection = Open();
        await connection.OpenAsync();

        var existing = await GetAttachmentAsync(connection, attachment.Id);
        if (existing is not null && File.Exists(existing.LocalPath))
        {
            return false;
        }

        if (await HasEquivalentAttachmentAsync(connection, attachment))
        {
            return false;
        }

        var extension = Path.GetExtension(attachment.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = attachment.Type == AttachmentType.Audio ? ".mp4" : ".jpg";
        }

        var localPath = Path.Combine(_mediaRoot, $"{attachment.Id:N}{extension}");
        await File.WriteAllBytesAsync(localPath, content);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachments (id, note_id, type, local_path, file_name, mime_type, size, is_uploaded)
            VALUES ($id, $note_id, $type, $local_path, $file_name, $mime_type, $size, 1)
            ON CONFLICT(id) DO UPDATE SET
                note_id = excluded.note_id,
                type = excluded.type,
                local_path = excluded.local_path,
                file_name = excluded.file_name,
                mime_type = excluded.mime_type,
                size = excluded.size,
                is_uploaded = 1
            """;
        command.Parameters.AddWithValue("$id", attachment.Id.ToString());
        command.Parameters.AddWithValue("$note_id", attachment.NoteId.ToString());
        command.Parameters.AddWithValue("$type", (int)attachment.Type);
        command.Parameters.AddWithValue("$local_path", localPath);
        command.Parameters.AddWithValue("$file_name", attachment.FileName);
        command.Parameters.AddWithValue("$mime_type", attachment.MimeType);
        command.Parameters.AddWithValue("$size", attachment.Size);
        await command.ExecuteNonQueryAsync();
        return true;
    }

    private SqliteConnection Open() => new($"Data Source={_dbPath}");

    private static string AudioMimeType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }

    private static string PhotoMimeType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg"
        };
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LocalNote?> GetNoteAsync(SqliteConnection connection, Guid id)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, date, start_time, end_time, created_at, updated_at, deleted_at,
                   sync_version, is_dirty, transcript_text, transcript_status
            FROM notes
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadNote(reader) : null;
    }

    private static async Task<LocalAttachment?> GetAttachmentAsync(SqliteConnection connection, Guid id)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, note_id, type, local_path, file_name, mime_type, size, is_uploaded
            FROM attachments
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAttachment(reader) : null;
    }

    private static async Task<bool> HasEquivalentAttachmentAsync(SqliteConnection connection, AttachmentDto attachment)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM attachments
            WHERE note_id = $note_id AND type = $type AND file_name = $file_name AND size = $size
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$note_id", attachment.NoteId.ToString());
        command.Parameters.AddWithValue("$type", (int)attachment.Type);
        command.Parameters.AddWithValue("$file_name", attachment.FileName);
        command.Parameters.AddWithValue("$size", attachment.Size);
        var result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    private static LocalNote ReadNote(SqliteDataReader reader)
    {
        return new LocalNote
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Body = reader.GetString(2),
            Date = DateOnly.Parse(reader.GetString(3)),
            StartTime = TimeOnly.Parse(reader.GetString(4)),
            EndTime = reader.IsDBNull(5) ? null : TimeOnly.Parse(reader.GetString(5)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7)),
            DeletedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            SyncVersion = reader.GetInt64(9),
            IsDirty = reader.GetInt64(10) == 1,
            TranscriptText = reader.IsDBNull(11) ? null : reader.GetString(11),
            TranscriptStatus = (TranscriptStatus)reader.GetInt32(12)
        };
    }

    private static LocalAttachment ReadAttachment(SqliteDataReader reader)
    {
        return new LocalAttachment
        {
            Id = Guid.Parse(reader.GetString(0)),
            NoteId = Guid.Parse(reader.GetString(1)),
            Type = (AttachmentType)reader.GetInt32(2),
            LocalPath = reader.GetString(3),
            FileName = reader.GetString(4),
            MimeType = reader.GetString(5),
            Size = reader.GetInt64(6),
            IsUploaded = reader.GetInt64(7) == 1
        };
    }

    private static void BindNote(SqliteCommand command, LocalNote note)
    {
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$title", note.Title);
        command.Parameters.AddWithValue("$body", note.Body);
        command.Parameters.AddWithValue("$date", note.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$start", note.StartTime.ToString("HH:mm:ss"));
        command.Parameters.AddWithValue("$end", note.EndTime?.ToString("HH:mm:ss") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$created", note.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", note.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$deleted", note.DeletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$version", note.SyncVersion);
        command.Parameters.AddWithValue("$text", note.TranscriptText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)note.TranscriptStatus);
    }
}
