using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectCal.Api.Data;

namespace ProjectCal.Api.Configuration;

public static class DatabaseConfiguration
{
    public static void UseProjectCalDatabase(
        this DbContextOptionsBuilder options,
        IConfiguration configuration,
        string defaultSqliteConnectionString)
    {
        var provider = ResolveProvider(configuration);
        var connectionString = ResolveConnectionString(configuration, provider, defaultSqliteConnectionString);

        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(ResolveSqliteConnectionString(connectionString));
            return;
        }

        options.UseNpgsql(connectionString);
    }

    public static string ResolveProvider(IConfiguration configuration)
    {
        if (HasExternalPostgresConnection(configuration))
        {
            return "Postgres";
        }

        return configuration["Database:Provider"] ?? "Postgres";
    }

    private static string ResolveConnectionString(
        IConfiguration configuration,
        string provider,
        string defaultSqliteConnectionString)
    {
        if (!string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var externalConnection =
                configuration["SUPABASE_DB_CONNECTION_STRING"]
                ?? configuration["DATABASE_URL"]
                ?? configuration["POSTGRES_URL"];

            if (!string.IsNullOrWhiteSpace(externalConnection))
            {
                return NormalizePostgresConnectionString(externalConnection);
            }
        }

        return configuration.GetConnectionString("Default")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
                ? defaultSqliteConnectionString
                : "Host=localhost;Port=5432;Database=projectcal;Username=projectcal;Password=projectcal");
    }

    private static bool HasExternalPostgresConnection(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["SUPABASE_DB_CONNECTION_STRING"])
            || !string.IsNullOrWhiteSpace(configuration["DATABASE_URL"])
            || !string.IsNullOrWhiteSpace(configuration["POSTGRES_URL"]);
    }

    private static string NormalizePostgresConnectionString(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
            || (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            return EnsureSsl(connectionString);
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            SslMode = SslMode.Require
        };

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        return builder.ConnectionString;
    }

    private static string EnsureSsl(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.SslMode == SslMode.Disable)
        {
            builder.SslMode = SslMode.Require;
        }

        return builder.ConnectionString;
    }

    private static string ResolveSqliteConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || Path.IsPathRooted(dataSource))
        {
            EnsureDirectory(dataSource);
            return builder.ToString();
        }

        var currentRelative = Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
        if (File.Exists(currentRelative))
        {
            builder.DataSource = currentRelative;
            return builder.ToString();
        }

        var projectRelative = Path.GetFullPath(dataSource, AppContext.BaseDirectory);
        if (File.Exists(projectRelative))
        {
            builder.DataSource = projectRelative;
            return builder.ToString();
        }

        var apiDatabase = FindUpwards("src", "ProjectCal.Api", Path.GetFileName(dataSource));
        if (apiDatabase is not null)
        {
            builder.DataSource = apiDatabase;
            return builder.ToString();
        }

        builder.DataSource = currentRelative;
        EnsureDirectory(builder.DataSource);
        return builder.ToString();
    }

    private static string? FindUpwards(params string[] pathParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void EnsureDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
