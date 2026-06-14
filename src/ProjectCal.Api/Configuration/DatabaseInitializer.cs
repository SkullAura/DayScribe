using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
            if (!await HasProjectCalTablesAsync(db, cancellationToken))
            {
                var creator = db.GetService<IRelationalDatabaseCreator>();
                await creator.CreateTablesAsync(cancellationToken);
            }

            return;
        }

        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static async Task<bool> HasProjectCalTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.\"Transcripts\"') IS NOT NULL";
        command.CommandTimeout = 10;

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}
