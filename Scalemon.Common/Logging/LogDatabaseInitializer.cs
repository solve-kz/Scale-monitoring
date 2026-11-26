using Microsoft.Data.Sqlite;
using System.IO;

namespace Scalemon.Common.Logging;

public static class LogDatabaseInitializer
{
    public const string TableName = "Logs";

    public static string NormalizeDatabasePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var directory = Path.Combine(Scalemon.Common.Updates.InstallationPaths.Root, "Logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "mainlogs.db");
        }

        var fullPath = Path.GetFullPath(configuredPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return fullPath;
    }

    public static void EnsureDatabase(string databasePath)
    {
        var normalized = NormalizeDatabasePath(databasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = normalized
        }.ToString());
        connection.Open();
        EnsureSchema(connection);
    }

    public static string BuildConnectionString(string databasePath)
    {
        var normalized = NormalizeDatabasePath(databasePath);
        return new SqliteConnectionStringBuilder { DataSource = normalized }.ToString();
    }

    public static void EnsureSchema(SqliteConnection connection)
    {
        using (var table = connection.CreateCommand())
        {
            table.CommandText = $@"CREATE TABLE IF NOT EXISTS {TableName} (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    Level TEXT NOT NULL,
    Source TEXT NULL,
    Message TEXT NULL,
    Exception TEXT NULL
);";
            table.ExecuteNonQuery();
        }

        using (var indexTs = connection.CreateCommand())
        {
            indexTs.CommandText = $"CREATE INDEX IF NOT EXISTS IX_{TableName}_Timestamp ON {TableName}(Timestamp);";
            indexTs.ExecuteNonQuery();
        }

        using (var indexLevel = connection.CreateCommand())
        {
            indexLevel.CommandText = $"CREATE INDEX IF NOT EXISTS IX_{TableName}_Level ON {TableName}(Level);";
            indexLevel.ExecuteNonQuery();
        }
    }
}
