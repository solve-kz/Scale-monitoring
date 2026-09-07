using System.Security.Cryptography;

namespace Scalemon.Common.Updates;

/// <summary>Проверяет полный сохранённый комплект программы перед переключением и откатом.</summary>
public static class VersionInventory
{
    private const string FileName = "version-integrity.json";
    public sealed record Entry(long Length, string Sha256);
    /// <summary>Создаёт контрольный список полного каталога версии.</summary>
    public static void Create(string directory)
    {
        var entries = Files(directory).ToDictionary(f => Path.GetRelativePath(directory, f),
            f => new Entry(new FileInfo(f).Length, Hash(f)), StringComparer.OrdinalIgnoreCase);
        if (!entries.ContainsKey("Scalemon.ServiceHost.exe")) throw new InvalidDataException("Нет исполняемого файла службы.");
        AtomicJson.Write(Path.Combine(directory, FileName), entries);
    }
    /// <summary>Проверяет состав, размеры и SHA-256 всех файлов версии.</summary>
    public static void Verify(string directory)
    {
        var expected = AtomicJson.Read<Dictionary<string, Entry>>(Path.Combine(directory, FileName))
            ?? throw new InvalidDataException("Нет контрольного списка версии. Требуется Setup.");
        var actual = Files(directory).ToDictionary(f => Path.GetRelativePath(directory, f), StringComparer.OrdinalIgnoreCase);
        if (expected.Count != actual.Count) throw new InvalidDataException("Состав сохранённой версии изменился.");
        foreach (var (name, entry) in expected)
            if (!actual.TryGetValue(name, out var file) || new FileInfo(file).Length != entry.Length || !Hash(file).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сохранённый комплект повреждён: " + name);
    }
    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static IEnumerable<string> Files(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в комплекте версии запрещены.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в комплекте версии запрещены.");
            if (attributes.HasFlag(FileAttributes.Directory)) { foreach (var child in Files(path)) yield return child; }
            else if (Path.GetFileName(path) != FileName && Path.GetFileName(path) != "installed-manifest.json") yield return path;
        }
    }
}

/// <summary>Политика восстановления; принятую версию нельзя автоматически откатывать из-за поздней ошибки.</summary>
public static class UpdateRecoveryPolicy
{
    /// <summary>Определяет этапы, на которых требуется восстановление регистрации службы.</summary>
    public static bool MayRestore(UpdateStage stage) => stage is UpdateStage.Stopped or UpdateStage.Switching or UpdateStage.Validating or UpdateStage.RollingBack;
    /// <summary>Определяет этапы, которые можно отменить без переключения файлов.</summary>
    public static bool MayCancel(UpdateStage stage) => stage is UpdateStage.Idle or UpdateStage.Prepared;
}
