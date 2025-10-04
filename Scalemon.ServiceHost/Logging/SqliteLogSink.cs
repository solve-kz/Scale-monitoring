using Microsoft.Data.Sqlite;
using Scalemon.Common.Logging;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Globalization;

namespace Scalemon.ServiceHost.Logging;

public sealed class SqliteLogSink : ILogEventSink
{
    private readonly string _connectionString;

    public SqliteLogSink(string databasePath)
    {
        LogDatabaseInitializer.EnsureDatabase(databasePath);
        _connectionString = LogDatabaseInitializer.BuildConnectionString(databasePath);
    }

    public void Emit(LogEvent logEvent)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $@"INSERT INTO {LogDatabaseInitializer.TableName}
(Timestamp, Level, Source, Message, Exception)
VALUES ($ts, $level, $source, $message, $exception);";

            command.Parameters.Add(new SqliteParameter("$ts", logEvent.Timestamp.ToUniversalTime().ToString("o")));
            command.Parameters.Add(new SqliteParameter("$level", logEvent.Level.ToString()));
            command.Parameters.Add(new SqliteParameter("$source", (object?)ExtractSource(logEvent) ?? DBNull.Value));
            command.Parameters.Add(new SqliteParameter("$message", logEvent.RenderMessage(CultureInfo.InvariantCulture)));
            command.Parameters.Add(new SqliteParameter("$exception", (object?)logEvent.Exception?.ToString() ?? DBNull.Value));

            command.ExecuteNonQuery();
        }
        catch
        {
            // избегаем рекурсивного логирования при ошибках записи
        }
    }

    private static string? ExtractSource(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var value))
        {
            return value switch
            {
                ScalarValue scalar => scalar.Value?.ToString(),
                _ => value.ToString()
            };
        }

        return null;
    }
}
