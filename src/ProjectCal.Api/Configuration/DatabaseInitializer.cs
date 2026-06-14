using Microsoft.EntityFrameworkCore;
using ProjectCal.Api.Data;

namespace ProjectCal.Api.Configuration;

public static class DatabaseInitializer
{
    public static async Task EnsureProjectCalSchemaAsync(AppDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Database:EnsureCreated", true))
        {
            return;
        }

        if (db.Database.IsNpgsql())
        {
            await EnsurePostgresSchemaAsync(db, cancellationToken);
            return;
        }

        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static async Task EnsurePostgresSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "Users" (
                "Id" uuid PRIMARY KEY,
                "Email" character varying(320) NOT NULL,
                "NormalizedEmail" character varying(320) NOT NULL,
                "PasswordHash" text NOT NULL,
                "EmailConfirmed" boolean NOT NULL DEFAULT TRUE,
                "EmailConfirmationTokenHash" text NULL,
                "EmailConfirmationExpiresAt" timestamp with time zone NULL,
                "PasswordResetTokenHash" text NULL,
                "PasswordResetExpiresAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "RefreshTokens" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "TokenHash" text NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "RevokedAt" timestamp with time zone NULL
            );

            CREATE TABLE IF NOT EXISTS "Notes" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "Title" character varying(200) NOT NULL,
                "Body" text NOT NULL,
                "Date" date NOT NULL,
                "StartTime" time without time zone NOT NULL,
                "EndTime" time without time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "DeletedAt" timestamp with time zone NULL,
                "SyncVersion" bigint NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Attachments" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "NoteId" uuid NOT NULL,
                "Type" integer NOT NULL,
                "FileName" character varying(260) NOT NULL,
                "StoredPath" text NOT NULL,
                "MimeType" character varying(160) NOT NULL,
                "Size" bigint NOT NULL,
                "UploadStatus" integer NOT NULL DEFAULT 1,
                "CreatedAt" timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Transcripts" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "NoteId" uuid NOT NULL,
                "AttachmentId" uuid NOT NULL,
                "Language" character varying(16) NOT NULL,
                "Text" text NULL,
                "Status" integer NOT NULL,
                "ErrorMessage" text NULL,
                "Attempts" integer NOT NULL DEFAULT 0,
                "UpdatedAt" timestamp with time zone NOT NULL
            );

            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "EmailConfirmed" boolean NOT NULL DEFAULT TRUE;
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "EmailConfirmationTokenHash" text NULL;
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "EmailConfirmationExpiresAt" timestamp with time zone NULL;
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetTokenHash" text NULL;
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetExpiresAt" timestamp with time zone NULL;

            ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "EndTime" time without time zone NULL;
            ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp with time zone NULL;
            ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "SyncVersion" bigint NOT NULL DEFAULT 0;

            ALTER TABLE "Attachments" ADD COLUMN IF NOT EXISTS "UploadStatus" integer NOT NULL DEFAULT 1;

            ALTER TABLE "Transcripts" ADD COLUMN IF NOT EXISTS "ErrorMessage" text NULL;
            ALTER TABLE "Transcripts" ADD COLUMN IF NOT EXISTS "Attempts" integer NOT NULL DEFAULT 0;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_NormalizedEmail" ON "Users" ("NormalizedEmail");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");
            CREATE INDEX IF NOT EXISTS "IX_Notes_UserId_Date_StartTime" ON "Notes" ("UserId", "Date", "StartTime");
            CREATE INDEX IF NOT EXISTS "IX_Notes_UserId_UpdatedAt" ON "Notes" ("UserId", "UpdatedAt");
            CREATE INDEX IF NOT EXISTS "IX_Attachments_UserId_NoteId" ON "Attachments" ("UserId", "NoteId");
            CREATE INDEX IF NOT EXISTS "IX_Transcripts_UserId_Status" ON "Transcripts" ("UserId", "Status");
            CREATE INDEX IF NOT EXISTS "IX_Transcripts_UserId_UpdatedAt" ON "Transcripts" ("UserId", "UpdatedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Transcripts_AttachmentId" ON "Transcripts" ("AttachmentId");
            """;

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
