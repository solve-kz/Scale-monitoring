using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Scalemon.Common.Logging;

public sealed class DailyLogDatabaseProvider
{
    private readonly string _basePath;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly string _extension;
    private readonly string _pattern;
    private readonly ConcurrentDictionary<string, string> _connectionStrings = new();

    public DailyLogDatabaseProvider(string? configuredPath)
    {
        _basePath = LogDatabaseInitializer.NormalizeDatabasePath(configuredPath);
        _directory = Path.GetDirectoryName(_basePath)!;
        Directory.CreateDirectory(_directory);

        var fileName = Path.GetFileName(_basePath);
        _extension = Path.GetExtension(fileName);
        _prefix = _extension.Length > 0 ? fileName[..^_extension.Length] : fileName;
        _pattern = string.IsNullOrEmpty(_extension)
            ? _prefix + "*"
            : _prefix + "*" + _extension;
    }

    public string BasePath => _basePath;

    public void EnsureCurrentDatabase()
    {
        var path = GetPathForDate(DateTime.Today);
        LogDatabaseInitializer.EnsureDatabase(path);
    }

    public string GetConnectionString(DateTime date)
    {
        var path = GetPathForDate(date.Date);
        return GetConnectionStringForPath(path);
    }

    public string GetConnectionStringForPath(string path)
    {
        return _connectionStrings.GetOrAdd(path, EnsureAndBuildConnectionString);
    }

    public string GetPathForDate(DateTime date)
    {
        var name = _prefix + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + _extension;
        return Path.Combine(_directory, name);
    }

    public IReadOnlyList<LogDatabaseFile> EnumerateDatabases(DateTime? from = null, DateTime? to = null)
    {
        var files = Directory.GetFiles(_directory, _pattern);
        var result = new List<LogDatabaseFile>(files.Length);

        foreach (var file in files)
        {
            if (TryCreateFileInfo(file, out var info))
            {
                if (from.HasValue && info.Date < from.Value.Date) continue;
                if (to.HasValue && info.Date > to.Value.Date) continue;
                result.Add(info);
            }
        }

        result.Sort(static (a, b) => b.Date.CompareTo(a.Date));
        return result;
    }

    private bool TryCreateFileInfo(string path, out LogDatabaseFile info)
    {
        info = default;
        try
        {
            var fileName = Path.GetFileName(path);
            var suffixLength = fileName.Length - _prefix.Length - _extension.Length;
            DateTime date;
            if (suffixLength >= 8)
            {
                var suffix = fileName.Substring(_prefix.Length, 8);
                if (DateTime.TryParseExact(suffix, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var parsed))
                {
                    date = parsed.Date;
                }
                else
                {
                    date = File.GetLastWriteTime(path).Date;
                }
            }
            else
            {
                date = File.GetLastWriteTime(path).Date;
            }

            info = new LogDatabaseFile(path, date);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string EnsureAndBuildConnectionString(string path)
    {
        LogDatabaseInitializer.EnsureDatabase(path);
        return LogDatabaseInitializer.BuildConnectionString(path);
    }

    public readonly record struct LogDatabaseFile(string Path, DateTime Date);
}
