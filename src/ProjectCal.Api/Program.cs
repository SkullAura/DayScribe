using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ProjectCal.Api;
using ProjectCal.Api.Configuration;
using ProjectCal.Api.Data;
using ProjectCal.Api.Data.Entities;
using ProjectCal.Api.Services;
using ProjectCal.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseProjectCalDatabase(builder.Configuration, "Data Source=projectcal.db");
});
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<IServerTranscriptionService, GroqServerTranscriptionService>();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
    });
});
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "ProjectCal",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ProjectCal.Client",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TokenService.GetSigningKey(builder.Configuration)))
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Configuration.GetValue("Database:EnsureCreated", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.EnsureProjectCalSchemaAsync(db, app.Configuration);
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "ProjectCal.Api",
    time = DateTimeOffset.UtcNow
}));

var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

auth.MapPost("/register", async (RegisterRequest request, AppDbContext db, CancellationToken ct) =>
{
    var email = request.Email.Trim();
    var normalizedEmail = email.ToUpperInvariant();
    if (email.Length < 5 || request.Password.Length < 8)
    {
        return Results.BadRequest(new { error = "Email and an 8+ character password are required." });
    }

    if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail, ct))
    {
        return Results.Conflict(new { error = "Email is already registered." });
    }

    var user = new UserEntity
    {
        Email = email,
        NormalizedEmail = normalizedEmail,
        PasswordHash = PasswordService.HashPassword(request.Password),
        EmailConfirmed = true
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new RegisterResponse(user.Id, user.Email, user.EmailConfirmed, null));
});

auth.MapPost("/confirm-email", async (ConfirmEmailRequest request, AppDbContext db, CancellationToken ct) =>
{
    var normalizedEmail = request.Email.Trim().ToUpperInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct);
    if (user is null || user.EmailConfirmationExpiresAt < DateTimeOffset.UtcNow)
    {
        return Results.BadRequest(new { error = "Invalid confirmation token." });
    }

    if (user.EmailConfirmationTokenHash != PasswordService.HashToken(request.Token))
    {
        return Results.BadRequest(new { error = "Invalid confirmation token." });
    }

    user.EmailConfirmed = true;
    user.EmailConfirmationTokenHash = null;
    user.EmailConfirmationExpiresAt = null;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

auth.MapPost("/login", async (LoginRequest request, AppDbContext db, TokenService tokens, CancellationToken ct) =>
{
    var normalizedEmail = request.Email.Trim().ToUpperInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct);
    if (user is null || !PasswordService.VerifyPassword(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var refreshToken = PasswordService.NewToken();
    db.RefreshTokens.Add(new RefreshTokenEntity
    {
        UserId = user.Id,
        TokenHash = PasswordService.HashToken(refreshToken),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
    });

    var access = tokens.CreateAccessToken(user);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new AuthResponse(access.AccessToken, refreshToken, access.ExpiresAt, new UserProfileDto(user.Id, user.Email, user.EmailConfirmed)));
});

auth.MapPost("/refresh", async (RefreshTokenRequest request, AppDbContext db, TokenService tokens, CancellationToken ct) =>
{
    var tokenHash = PasswordService.HashToken(request.RefreshToken);
    var refresh = await db.RefreshTokens.Include(x => x.User).FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);
    if (refresh?.User is null || refresh.RevokedAt is not null || refresh.ExpiresAt < DateTimeOffset.UtcNow)
    {
        return Results.Unauthorized();
    }

    refresh.RevokedAt = DateTimeOffset.UtcNow;
    var nextRefreshToken = PasswordService.NewToken();
    db.RefreshTokens.Add(new RefreshTokenEntity
    {
        UserId = refresh.UserId,
        TokenHash = PasswordService.HashToken(nextRefreshToken),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
    });

    var access = tokens.CreateAccessToken(refresh.User);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new AuthResponse(access.AccessToken, nextRefreshToken, access.ExpiresAt, new UserProfileDto(refresh.User.Id, refresh.User.Email, refresh.User.EmailConfirmed)));
});

auth.MapPost("/forgot-password", async (ForgotPasswordRequest request, AppDbContext db, CancellationToken ct) =>
{
    var normalizedEmail = request.Email.Trim().ToUpperInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct);
    if (user is null)
    {
        return Results.Ok(new ForgotPasswordResponse("If the email exists, reset instructions were created.", null));
    }

    var token = PasswordService.NewToken();
    user.PasswordResetTokenHash = PasswordService.HashToken(token);
    user.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new ForgotPasswordResponse("Password reset token created.", token));
});

auth.MapPost("/reset-password", async (ResetPasswordRequest request, AppDbContext db, CancellationToken ct) =>
{
    var normalizedEmail = request.Email.Trim().ToUpperInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, ct);
    if (user is null || user.PasswordResetExpiresAt < DateTimeOffset.UtcNow || user.PasswordResetTokenHash != PasswordService.HashToken(request.Token))
    {
        return Results.BadRequest(new { error = "Invalid reset token." });
    }

    user.PasswordHash = PasswordService.HashPassword(request.NewPassword);
    user.PasswordResetTokenHash = null;
    user.PasswordResetExpiresAt = null;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

var notes = app.MapGroup("/api/notes").RequireAuthorization();

notes.MapGet("/", async (DateOnly? date, string? search, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var query = db.Notes
        .AsNoTracking()
        .Include(x => x.Attachments)
        .Include(x => x.Transcript)
        .Where(x => x.UserId == userId && x.DeletedAt == null);

    if (date is not null)
    {
        query = query.Where(x => x.Date == date);
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(x => x.Title.Contains(search) || x.Body.Contains(search) || (x.Transcript != null && x.Transcript.Text != null && x.Transcript.Text.Contains(search)));
    }

    var result = await query.OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToArrayAsync(ct);
    return Results.Ok(result.Select(x => x.ToDto()).ToArray());
});

notes.MapPost("/", async (UpsertNoteRequest request, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var now = DateTimeOffset.UtcNow;
    var note = new NoteEntity
    {
        Id = request.Id ?? Guid.NewGuid(),
        UserId = userId,
        Title = request.Title.Trim(),
        Body = request.Body,
        Date = request.Date,
        StartTime = request.StartTime,
        EndTime = request.EndTime,
        CreatedAt = now,
        UpdatedAt = now,
        SyncVersion = request.SyncVersion + 1
    };

    db.Notes.Add(note);
    await TranscriptSync.ApplyFromNoteRequestAsync(note, request, db, userId, now, ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/notes/{note.Id}", note.ToDto());
});

notes.MapPut("/{id:guid}", async (Guid id, UpsertNoteRequest request, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var note = await db.Notes.Include(x => x.Attachments).Include(x => x.Transcript).FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    if (note is null)
    {
        return Results.NotFound();
    }

    note.Title = request.Title.Trim();
    note.Body = request.Body;
    note.Date = request.Date;
    note.StartTime = request.StartTime;
    note.EndTime = request.EndTime;
    note.UpdatedAt = DateTimeOffset.UtcNow;
    note.SyncVersion = Math.Max(note.SyncVersion, request.SyncVersion) + 1;
    note.DeletedAt = null;
    await TranscriptSync.ApplyFromNoteRequestAsync(note, request, db, userId, note.UpdatedAt, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(note.ToDto());
});

notes.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    if (note is null)
    {
        return Results.NotFound();
    }

    note.DeletedAt = DateTimeOffset.UtcNow;
    note.UpdatedAt = DateTimeOffset.UtcNow;
    note.SyncVersion++;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

notes.MapPost("/{noteId:guid}/attachments", async (Guid noteId, IFormFile file, AttachmentType type, string? language, AppDbContext db, IFileStorage storage, IServerTranscriptionService transcription, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == noteId && x.UserId == userId && x.DeletedAt == null, ct);
    if (note is null)
    {
        return Results.NotFound();
    }

    var attachment = new AttachmentEntity
    {
        UserId = userId,
        NoteId = noteId,
        Type = type,
        FileName = Path.GetFileName(file.FileName),
        MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
        Size = file.Length
    };

    attachment.StoredPath = await storage.SaveAsync(userId, attachment.Id, file, ct);
    db.Attachments.Add(attachment);

    if (type == AttachmentType.Audio)
    {
        var existingTranscript = await db.Transcripts.FirstOrDefaultAsync(x => x.NoteId == noteId && x.UserId == userId, ct);
        var transcriptLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language;
        var now = DateTimeOffset.UtcNow;
        if (existingTranscript is null)
        {
            existingTranscript = new TranscriptEntity
            {
                UserId = userId,
                NoteId = noteId,
                AttachmentId = attachment.Id,
                Language = transcriptLanguage
            };
            db.Transcripts.Add(existingTranscript);
        }

        existingTranscript.AttachmentId = attachment.Id;
        existingTranscript.Language = transcriptLanguage;
        existingTranscript.Text = null;
        existingTranscript.ErrorMessage = null;
        existingTranscript.Status = TranscriptStatus.Processing;
        existingTranscript.UpdatedAt = now;

        var storedFile = await storage.OpenAsync(attachment, ct);
        if (storedFile is null)
        {
            existingTranscript.Status = TranscriptStatus.Failed;
            existingTranscript.ErrorMessage = "Stored audio file was not found.";
        }
        else
        {
            await using var stream = storedFile.Value.Stream;
            try
            {
                var text = await transcription.TranscribeAsync(
                    stream,
                    storedFile.Value.FileName,
                    storedFile.Value.MimeType,
                    transcriptLanguage,
                    ct);
                existingTranscript.Text = text;
                existingTranscript.Status = string.IsNullOrWhiteSpace(text) ? TranscriptStatus.Failed : TranscriptStatus.Done;
                existingTranscript.ErrorMessage = string.IsNullOrWhiteSpace(text) ? "Groq returned empty text." : null;
            }
            catch (Exception ex)
            {
                existingTranscript.Status = TranscriptStatus.Failed;
                existingTranscript.ErrorMessage = ex.Message;
            }
        }

        existingTranscript.UpdatedAt = DateTimeOffset.UtcNow;
    }

    note.UpdatedAt = DateTimeOffset.UtcNow;
    note.SyncVersion++;
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/attachments/{attachment.Id}", attachment.ToDto());
}).DisableAntiforgery();

notes.MapPost("/{noteId:guid}/transcript", async (Guid noteId, UpsertTranscriptRequest request, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == noteId && x.UserId == userId && x.DeletedAt == null, ct);
    if (note is null)
    {
        return Results.NotFound();
    }

    var transcript = await TranscriptSync.UpsertAsync(note, request, db, userId, DateTimeOffset.UtcNow, requireAudio: true, ct);
    if (transcript is null)
    {
        return Results.Conflict(new { error = "Upload an audio attachment before syncing a transcript." });
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(transcript.ToDto());
});

app.MapGet("/api/attachments/{id:guid}/download", async (Guid id, AppDbContext db, IFileStorage storage, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var attachment = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    if (attachment is null)
    {
        return Results.NotFound();
    }

    var file = await storage.OpenAsync(attachment, ct);
    return file is null ? Results.NotFound() : Results.File(file.Value.Stream, file.Value.MimeType, file.Value.FileName);
}).RequireAuthorization();

app.MapPost("/api/sync", async (SyncRequest request, AppDbContext db, HttpContext http, CancellationToken ct) =>
{
    var userId = http.User.GetUserId();
    var now = DateTimeOffset.UtcNow;

    foreach (var mutation in request.Notes)
    {
        var noteId = mutation.Note.Id ?? Guid.NewGuid();
        var existing = await db.Notes.FirstOrDefaultAsync(x => x.Id == noteId && x.UserId == userId, ct);

        if (mutation.Operation == SyncOperation.Delete)
        {
            if (existing is not null)
            {
                existing.DeletedAt = now;
                existing.UpdatedAt = now;
                existing.SyncVersion++;
            }

            continue;
        }

        if (existing is null)
        {
            var note = new NoteEntity
            {
                Id = noteId,
                UserId = userId,
                Title = mutation.Note.Title,
                Body = mutation.Note.Body,
                Date = mutation.Note.Date,
                StartTime = mutation.Note.StartTime,
                EndTime = mutation.Note.EndTime,
                CreatedAt = now,
                UpdatedAt = now,
                SyncVersion = mutation.Note.SyncVersion + 1
            };
            db.Notes.Add(note);
            await TranscriptSync.ApplyFromNoteRequestAsync(note, mutation.Note, db, userId, now, ct);
        }
        else if (mutation.Note.SyncVersion >= existing.SyncVersion)
        {
            existing.Title = mutation.Note.Title;
            existing.Body = mutation.Note.Body;
            existing.Date = mutation.Note.Date;
            existing.StartTime = mutation.Note.StartTime;
            existing.EndTime = mutation.Note.EndTime;
            existing.UpdatedAt = now;
            existing.DeletedAt = null;
            existing.SyncVersion = mutation.Note.SyncVersion + 1;
            await TranscriptSync.ApplyFromNoteRequestAsync(existing, mutation.Note, db, userId, now, ct);
        }
    }

    await db.SaveChangesAsync(ct);

    var since = request.LastSyncAt ?? DateTimeOffset.MinValue;
    var changedNoteEntities = await db.Notes
        .AsNoTracking()
        .Include(x => x.Attachments)
        .Include(x => x.Transcript)
        .Where(x => x.UserId == userId)
        .ToArrayAsync(ct);
    changedNoteEntities = changedNoteEntities.Where(x => x.UpdatedAt > since).OrderBy(x => x.UpdatedAt).ToArray();

    var changedAttachmentEntities = await db.Attachments
        .AsNoTracking()
        .Where(x => x.UserId == userId)
        .ToArrayAsync(ct);
    changedAttachmentEntities = changedAttachmentEntities.Where(x => x.CreatedAt > since).ToArray();

    var changedTranscriptEntities = await db.Transcripts
        .AsNoTracking()
        .Where(x => x.UserId == userId)
        .ToArrayAsync(ct);
    changedTranscriptEntities = changedTranscriptEntities.Where(x => x.UpdatedAt > since).ToArray();

    return Results.Ok(new SyncResponse(
        now,
        changedNoteEntities.Select(x => x.ToDto()).ToArray(),
        changedAttachmentEntities.Select(x => x.ToDto()).ToArray(),
        changedTranscriptEntities.Select(x => x.ToDto()).ToArray()));
}).RequireAuthorization();

app.MapGet("/api/admin/stats", async (AppDbContext db, CancellationToken ct) =>
{
    var queue = new QueueStatsDto(
        await db.Transcripts.CountAsync(x => x.Status == TranscriptStatus.Pending || x.Status == TranscriptStatus.Processing, ct),
        await db.Transcripts.CountAsync(x => x.Status == TranscriptStatus.Failed, ct));

    return new AdminStatsDto(
        await db.Users.CountAsync(ct),
        await db.Notes.CountAsync(x => x.DeletedAt == null, ct),
        await db.Attachments.CountAsync(ct),
        queue);
});

app.Run();

public partial class Program;

internal static class TranscriptSync
{
    public static Task<TranscriptEntity?> ApplyFromNoteRequestAsync(
        NoteEntity note,
        UpsertNoteRequest request,
        AppDbContext db,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.TranscriptStatus is null
            || request.TranscriptStatus == TranscriptStatus.None
            || string.IsNullOrWhiteSpace(request.TranscriptText))
        {
            return Task.FromResult<TranscriptEntity?>(null);
        }

        return UpsertAsync(
            note,
            new UpsertTranscriptRequest(
                string.IsNullOrWhiteSpace(request.TranscriptLanguage) ? "auto" : request.TranscriptLanguage,
                request.TranscriptText,
                request.TranscriptStatus.Value),
            db,
            userId,
            now,
            requireAudio: false,
            cancellationToken);
    }

    public static async Task<TranscriptEntity?> UpsertAsync(
        NoteEntity note,
        UpsertTranscriptRequest request,
        AppDbContext db,
        Guid userId,
        DateTimeOffset now,
        bool requireAudio,
        CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments
            .Where(x => x.NoteId == note.Id && x.UserId == userId && x.Type == AttachmentType.Audio)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (attachment is null)
        {
            return null;
        }

        var transcript = await db.Transcripts.FirstOrDefaultAsync(x => x.NoteId == note.Id && x.UserId == userId, cancellationToken);
        if (transcript is null)
        {
            transcript = new TranscriptEntity
            {
                UserId = userId,
                NoteId = note.Id,
                AttachmentId = attachment.Id
            };
            db.Transcripts.Add(transcript);
        }

        transcript.AttachmentId = attachment.Id;
        transcript.Language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language;
        transcript.Text = request.Text;
        transcript.Status = request.Status;
        transcript.ErrorMessage = null;
        transcript.UpdatedAt = now;
        note.UpdatedAt = now;
        note.SyncVersion++;
        return transcript;
    }
}
