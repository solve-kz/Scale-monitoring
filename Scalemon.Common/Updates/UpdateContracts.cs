using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scalemon.Common.Updates;

/// <summary>Постоянные пути установки; данные не зависят от версии программы.</summary>
public static class InstallationPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scalemon");
    public static string Config => Path.Combine(Root, "Config");
    public static string Settings => Path.Combine(Config, "Scalemon.settings.json");
    /// <summary>Совместимость с ручной установкой; новые установки используют ProgramData.</summary>
    public static string ResolveSettings(string? configured = null)
    {
        if (File.Exists(Settings)) return Settings;
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        const string legacy = @"C:\Scalemon\Scalemon.settings.json";
        return File.Exists(legacy) ? legacy : Settings;
    }
    /// <summary>Возвращает путь постоянного файла данных.</summary>
    public static string DataFile(string name) => Path.Combine(Root, "Data", name);
    /// <summary>Возвращает путь постоянного файла журнала.</summary>
    public static string LogFile(string name) => Path.Combine(Root, "Logs", name);
    public static string Updates => Path.Combine(Root, "Updates");
    public static string Installation => Path.Combine(Updates, "installation.json");
    public static string Journal => Path.Combine(Updates, "operation.json");
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Scalemon");
    public static string Versions => Path.Combine(InstallRoot, "Versions");
    /// <summary>Возвращает информационную версию без идентификатора коммита.</summary>
    public static string Version => (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}

/// <summary>Настройки автоматической доставки; изменяются администратором.</summary>
public sealed record UpdateSettings
{
    public bool AutoDownload { get; init; } = true;
    public bool AutoInstall { get; init; } = true;
    public string Channel { get; init; } = "Stable";
    public int StartHour { get; init; } = 22;
    public int EndHour { get; init; } = 5;
    /// <summary>Проверяет допустимость настроек без произвольных адресов сервера.</summary>
    public void Validate()
    {
        if (Channel is not ("Stable" or "Test") || StartHour is < 0 or > 23 || EndHour is < 0 or > 23 || StartHour == EndHour)
            throw new InvalidDataException("Укажите канал Stable/Test и различающиеся часы от 0 до 23.");
    }
}

/// <summary>Подписанное описание полного пакета приложения.</summary>
public sealed record ReleaseManifest
{
    public string Version { get; init; } = "";
    public string GitSha { get; init; } = "";
    public string Channel { get; init; } = "Stable";
    public string Architecture { get; init; } = "win-x64";
    public string Archive { get; init; } = "";
    public long Size { get; init; }
    public long ExpandedSize { get; init; }
    public string Sha256 { get; init; } = "";
    public string MinimumUpdaterVersion { get; init; } = "1.0.0";
    public bool BackwardCompatible { get; init; }
    public string Notes { get; init; } = "";
    /// <summary>Проверяет формат и ограничения пакета.</summary>
    public void Validate()
    {
        _ = ReleaseVersion.Parse(Version);
        _ = ReleaseVersion.Parse(MinimumUpdaterVersion);
        if (Version != Version.ToLowerInvariant() || Architecture != "win-x64" || Channel is not ("Stable" or "Test") || !BackwardCompatible ||
            Archive != $"Scalemon-{Version}-win-x64.zip" || Size is <= 0 or > 2_147_483_648 ||
            ExpandedSize < Size || ExpandedSize > 8L * 1024 * 1024 * 1024 ||
            !Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$") || !Regex.IsMatch(GitSha, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("Неподдерживаемый или несовместимый пакет обновления.");
    }
}

/// <summary>Сравнивает версии SemVer, включая числовые части prerelease.</summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string Pre) : IComparable<ReleaseVersion>
{
    /// <summary>Разбирает строгое значение SemVer без build metadata.</summary>
    public static ReleaseVersion Parse(string value)
    {
        var m = Regex.Match(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$");
        if (!m.Success || value.Length > 100) throw new InvalidDataException("Некорректная версия.");
        var pre = m.Groups[4].Value;
        if (pre.Split('.').Any(p => p.Length > 1 && p.All(char.IsDigit) && p[0] == '0')) throw new InvalidDataException("Некорректный prerelease.");
        return new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), pre);
    }
    /// <summary>Сравнивает версии по правилам SemVer.</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (var n in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (n != 0) return n;
        if (Pre == other.Pre) return 0;
        if (Pre == "") return 1;
        if (other.Pre == "") return -1;
        var a = Pre.Split('.'); var b = other.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsDigit); var bn = b[i].All(char.IsDigit);
            var c = an && bn ? (a[i].Length != b[i].Length ? a[i].Length.CompareTo(b[i].Length) : string.CompareOrdinal(a[i], b[i]))
                : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}

/// <summary>Согласованный снимок готовности работающего процесса.</summary>
public sealed record ReadinessSnapshot(string Version, string Instance, DateTimeOffset ObservedAt,
    bool Connected, DateTimeOffset? DisconnectedSince, int ActiveOperations, int PendingWrites,
    bool DatabaseAvailable, bool Maintenance, bool Prepared, bool Healthy, string Reason);

/// <summary>Проверяемое правило установки, не зависящее от Windows-служб.</summary>
public static class UpdatePolicy
{
    /// <summary>Проверяет локальное расписание, включая окно через полночь.</summary>
    public static bool InWindow(UpdateSettings settings, DateTime localTime) => settings.StartHour < settings.EndHour
        ? localTime.Hour >= settings.StartHour && localTime.Hour < settings.EndHour
        : localTime.Hour >= settings.StartHour || localTime.Hour < settings.EndHour;
    /// <summary>Проверяет свежесть телеметрии, отключение оборудования и сохранность операций.</summary>
    public static bool Safe(ReadinessSnapshot s, DateTimeOffset now) =>
        now >= s.ObservedAt && now - s.ObservedAt < TimeSpan.FromSeconds(15) && !s.Connected &&
        s.DisconnectedSince.HasValue && now - s.DisconnectedSince.Value >= TimeSpan.FromMinutes(10) &&
        s.ActiveOperations == 0 && s.PendingWrites == 0 && s.DatabaseAvailable && s.Healthy;
}

/// <summary>Этапы устойчивой к перезапуску операции.</summary>
public enum UpdateStage { Idle, Prepared, Stopped, Switching, Validating, Accepted, RollingBack, Failed }
/// <summary>Журнал перехода; хранится до изменения регистрации службы.</summary>
public sealed record UpdateOperation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FromVersion { get; init; } = "";
    public string ToVersion { get; init; } = "";
    public string OriginalImagePath { get; init; } = "";
    public string ServiceName { get; init; } = "Scalemon";
    public UpdateStage Stage { get; init; }
    public string Initiator { get; init; } = "Automatic";
    public string? Error { get; init; }
}
/// <summary>Отображаемое состояние обновления.</summary>
public sealed record UpdateStatus(string Current, string? Previous, ReleaseManifest? Available,
    string State, string Message, long DownloadedBytes, UpdateSettings Settings, ReadinessSnapshot? Readiness,
    IReadOnlyList<string> History);
/// <summary>Команда локального протокола; произвольное выполнение команд не поддерживается.</summary>
public sealed record UpdateRequest(string Command, string? Version = null, UpdateSettings? Settings = null, string? Actor = null);
/// <summary>Сведения об установленной службе, записанные установщиком.</summary>
public sealed record InstallationRecord(string ServiceName, string CurrentVersion, string WebUrl);
/// <summary>Ответ локального протокола.</summary>
public sealed record UpdateReply(bool Success, string Message, UpdateStatus? Status = null, ReadinessSnapshot? Readiness = null);
/// <summary>Клиент административных операций.</summary>
public interface IUpdaterClient { Task<UpdateReply> SendAsync(UpdateRequest request, CancellationToken cancellationToken = default); }
/// <summary>Готовность к обновлению.</summary>
public interface IUpdateReadiness { ReadinessSnapshot Snapshot(); }
/// <summary>Барьер остановки и возврат в рабочий режим.</summary>
public interface IMaintenanceCoordinator : IUpdateReadiness
{
    Task PrepareAsync(CancellationToken cancellationToken);
    void Resume();
}
/// <summary>Учитывает записи, включая извлечённые из очереди и выполняемые.</summary>
public interface IDataWriteDrain
{
    int PendingWrites { get; }
    bool DatabaseAvailable { get; }
    Task DrainAsync(CancellationToken cancellationToken);
    Task StopWritesAsync(CancellationToken cancellationToken);
}
/// <summary>Атомарное сохранение небольших управляющих файлов.</summary>
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>Читает управляющий JSON либо возвращает пустое значение, если файла нет.</summary>
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) : default;
    /// <summary>Заменяет управляющий JSON атомарно после принудительной записи временного файла.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, Options); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
