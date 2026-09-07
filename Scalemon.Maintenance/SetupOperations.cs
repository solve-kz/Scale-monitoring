using System.Diagnostics;
using System.IO.Ports;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Scalemon.Common.Updates;

namespace Scalemon.Maintenance;

internal static class SetupOperations
{
    public static void Discover(string file)
    {
        Protect(Path.GetDirectoryName(file)!, null);
        var installed = AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation);
        var name = installed?.ServiceName;
        if (name is null)
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")!;
            foreach (var child in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(child);
                if ((key?.GetValue("ImagePath") as string)?.Contains("Scalemon.ServiceHost.exe", StringComparison.OrdinalIgnoreCase) == true)
                { name = child; break; }
            }
        }
        string? legacy = null;
        if (name is not null)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            if (key?.GetValue("ImagePath") is string image) legacy = Path.GetDirectoryName(ImageExecutable(image));
        }
        var settings = MergeSettings(legacy);
        File.WriteAllLines(file, new[] { "[Setup]", "Service=" + (name ?? "Scalemon"),
            "ScalePort=" + (Get(settings, "ScaleSettings:PortName") ?? "COM2"),
            "PlcPort=" + (Get(settings, "PlcSettings:PortName") ?? "COM3"),
            "Port=" + (Get(settings, "Api:Port") ?? "5000") });
    }
    private static async Task ScAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout; await stderr;
        if (process.ExitCode != 0) throw new IOException($"Ошибка управления службой Windows: {process.ExitCode}.");
    }
    private static bool Exists(string name)
    { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name); return key is not null; }
    private static async Task StopAsync(string name)
    {
        if (!Exists(name)) return;
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(120)));
    }
    private static async Task StartAsync(string name)
    {
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(120)));
    }
    private static Dictionary<string, string> ReadOptions(string file) => File.ReadAllLines(file)
        .Where(l => !l.StartsWith('[') && l.Contains('='))
        .Select(l => l.Split('=', 2)).ToDictionary(x => x[0].Trim(), x => x[1], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> InstallAsync(string source, string optionsFile)
    {
        var resultFile = Path.Combine(Path.GetDirectoryName(optionsFile)!, "result.txt");
        string? oldImage = null; string? name = null; string? backup = null; bool created = false;
        bool stopped = false;
        bool accepted = false;
        bool backupComplete = false;
        bool infrastructureReplaced = false;
        try
        {
            if (!Environment.Is64BitOperatingSystem || OperatingSystem.IsWindowsVersionAtLeast(10) == false)
                throw new InvalidOperationException("Нужна Windows 10/11 x64.");
            foreach (var directory in new[] { InstallationPaths.Root, InstallationPaths.Config, InstallationPaths.Updates,
                Path.Combine(InstallationPaths.Root, "Backups"), InstallationPaths.InstallRoot, InstallationPaths.Versions })
                if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Каталоги Scalemon не могут быть файловыми ссылками.");
            var options = ReadOptions(optionsFile);
            name = options.GetValueOrDefault("Service", "Scalemon");
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,80}$") || name == "Scalemon.Updater") throw new InvalidDataException("Недопустимое имя службы.");
            var version = File.ReadAllText(Path.Combine(source, "version.txt")).Trim(); _ = ReleaseVersion.Parse(version);
            if (version != version.ToLowerInvariant()) throw new InvalidDataException("Используйте нижний регистр в версии выпуска.");
            var port = int.Parse(options.GetValueOrDefault("Port", "5000"));
            if (port is < 1024 or > 65535) throw new InvalidDataException("Порт должен быть от 1024 до 65535.");
            var scalePort = options.GetValueOrDefault("ScalePort", "COM2");
            var plcPort = options.GetValueOrDefault("PlcPort", "COM3");
            if (!SerialPort.GetPortNames().Contains(scalePort, StringComparer.OrdinalIgnoreCase) || !SerialPort.GetPortNames().Contains(plcPort, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выбранные COM-порты не найдены. Установите драйверы и подключите интерфейсы оборудования.");
            if (scalePort.Equals(plcPort, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Для весов и Arduino нужны разные порты.");
            var target = Path.Combine(InstallationPaths.Versions, version);
            var priorRecord = AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation);
            if (priorRecord is not null && priorRecord.ServiceName != name) throw new InvalidOperationException("На компьютере уже установлена другая служба Scalemon.");
            var repair = Directory.Exists(target);
            if (repair && priorRecord is null) throw new InvalidOperationException("Каталог версии уже существует без зарегистрированной установки. Сохраните его отдельно перед повтором.");
            if (Exists(name))
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name)!;
                oldImage = key.GetValue("ImagePath") as string;
                if (oldImage is null || !oldImage.Contains("Scalemon.ServiceHost.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Указанная служба не является Scalemon.ServiceHost.");
            }
            var legacyDirForSpace = oldImage is null ? null : Path.GetDirectoryName(ImageExecutable(oldImage));
            long size = TreeSize(source) + TreeSize(InstallationPaths.Config) + TreeSize(InstallationPaths.Updates) + TreeSize(legacyDirForSpace);
            if (new DriveInfo(Path.GetPathRoot(InstallationPaths.InstallRoot)!).AvailableFreeSpace < size * 2 + 104857600 ||
                new DriveInfo(Path.GetPathRoot(InstallationPaths.Root)!).AvailableFreeSpace < size * 2 + 104857600)
                throw new IOException("Недостаточно места для установки и резервной копии.");
            var oldVersion = priorRecord is null || oldImage is null ? version : Path.GetFileName(Path.GetDirectoryName(ImageExecutable(oldImage)))!;
            if (priorRecord is not null && ReleaseVersion.Parse(version).CompareTo(ReleaseVersion.Parse(oldVersion)) < 0)
                throw new InvalidOperationException("Установщик старее текущей версии. Для возврата используйте обслуживание Scalemon.");
            // При подключённой инфраструктуре используем тот же барьер, что и автоматические обновления.
            if (priorRecord is not null && Exists(name))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
                var prepared = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("prepare"), timeout.Token);
                if (!prepared.Success) throw new InvalidOperationException("Старая версия не готова: " + prepared.Message);
            }
            await StopAsync("Scalemon.Updater"); await StopAsync(name); stopped = true;
            backup = Path.Combine(InstallationPaths.Root, "Backups", "setup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backup); Protect(backup, null);
            if (Directory.Exists(InstallationPaths.Config)) CopyTree(InstallationPaths.Config, Path.Combine(backup, "Config"));
            if (Directory.Exists(InstallationPaths.Updates)) CopyTree(InstallationPaths.Updates, Path.Combine(backup, "Updates"));
            var installedInfrastructure = Path.Combine(InstallationPaths.InstallRoot, "Infrastructure");
            if (Directory.Exists(installedInfrastructure)) CopyTree(installedInfrastructure, Path.Combine(backup, "Infrastructure"));
            var legacyDir = oldImage is null ? null : Path.GetDirectoryName(ImageExecutable(oldImage));
            if (legacyDir is not null)
            {
                CopyTree(legacyDir, Path.Combine(backup, "Application"));
                using var oldKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name)!;
                AtomicJson.Write(Path.Combine(backup, "service.json"), new { Name = name, ImagePath = oldImage,
                    Account = oldKey.GetValue("ObjectName"), Start = oldKey.GetValue("Start") });
            }
            backupComplete = true;
            foreach (var sub in new[] { "Config", "Data", "Logs", "Updates", "Backups" }) Directory.CreateDirectory(Path.Combine(InstallationPaths.Root, sub));
            AtomicJson.Write(InstallationPaths.Journal, new UpdateOperation { FromVersion = oldVersion, ToVersion = version, ServiceName = name,
                OriginalImagePath = oldImage ?? Quote(Path.Combine(target, "Scalemon.ServiceHost.exe")), Stage = UpdateStage.Prepared, Initiator = "Setup" });
            var settings = MergeSettings(legacyDir);
            Set(settings, "ScaleSettings:PortName", scalePort); Set(settings, "PlcSettings:PortName", plcPort);
            Set(settings, "Api:Port", port); Set(settings, "Api:Host", "localhost"); Set(settings, "Urls", $"http://0.0.0.0:{port}");
            if (!string.IsNullOrWhiteSpace(options.GetValueOrDefault("Connection"))) Set(settings, "DatabaseSettings:ConnectionString", options["Connection"]);
            if (string.IsNullOrWhiteSpace(Get(settings, "DatabaseSettings:ConnectionString"))) throw new InvalidDataException("Введите строку подключения SQL Server.");
            if (string.IsNullOrWhiteSpace(Get(settings, "DatabaseSettings:TableName"))) Set(settings, "DatabaseSettings:TableName", "Weighings");
            if (legacyDir is null)
            {
                var password = options.GetValueOrDefault("AdminPassword", "");
                if (password.Length < 12) throw new InvalidDataException("Задайте пароль администратора не короче 12 символов.");
                Set(settings, "Authentication:InitialAdmin:Password", password);
                Set(settings, "Authentication:InitialAdmin:Login", "admin");
            }
            MigrateLocalData(settings, legacyDir, priorRecord is null && legacyDir is not null);
            AtomicJson.Write(InstallationPaths.Settings, settings);
            if (!repair) { CopyTree(Path.Combine(source, "Application"), target); VersionInventory.Create(target); }
            else VersionInventory.Verify(target);
            infrastructureReplaced = true;
            ReplaceInfrastructure(Path.Combine(source, "Infrastructure"), installedInfrastructure);
            if (!Exists(name))
            {
                await ScAsync("create", name, "binPath=", Quote(Path.Combine(target, "Scalemon.ServiceHost.exe")), "start=", "auto", "obj=", @"NT SERVICE\" + name);
                created = true;
            }
            else await ScAsync("config", name, "binPath=", Quote(Path.Combine(target, "Scalemon.ServiceHost.exe")), "start=", "auto");
            await ScAsync("sidtype", name, "unrestricted");
            // Сохраняет существующую учётную запись; ACL выдаются service SID.
            foreach (var sub in new[] { "Data", "Logs" }) Protect(Path.Combine(InstallationPaths.Root, sub), name);
            Protect(InstallationPaths.Config, name);
            Protect(InstallationPaths.Updates, null, name);
            Protect(Path.Combine(InstallationPaths.Root, "Backups"), null);
            var infrastructure = Path.Combine(InstallationPaths.InstallRoot, "Infrastructure");
            if (Exists("Scalemon.Updater")) await ScAsync("config", "Scalemon.Updater", "binPath=", Quote(Path.Combine(infrastructure, "Updater", "Scalemon.Updater.exe")), "start=", "auto");
            else await ScAsync("create", "Scalemon.Updater", "binPath=", Quote(Path.Combine(infrastructure, "Updater", "Scalemon.Updater.exe")), "start=", "auto", "obj=", "LocalSystem");
            foreach (var svc in new[] { name, "Scalemon.Updater" })
                await ScAsync("failure", svc, "reset=", "86400", "actions=", "restart/60000/restart/60000/none/0");
            AtomicJson.Write(InstallationPaths.Installation, new InstallationRecord(name, version, $"http://localhost:{port}"));
            var setupOperation = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal)!;
            AtomicJson.Write(InstallationPaths.Journal, setupOperation with { Stage = UpdateStage.Validating });
            await StartAsync(name);
            using (var check = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                UpdateReply? reply = null;
                while (!check.IsCancellationRequested)
                {
                    try { reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("dependencies"), check.Token); if (reply.Success) break; }
                    catch (IOException) { } catch (TimeoutException) { }
                    await Task.Delay(1000, check.Token);
                }
                if (reply?.Success != true) throw new InvalidOperationException("Проверка SQL и локальных данных от имени службы не прошла.");
                var op = AtomicJson.Read<UpdateOperation>(InstallationPaths.Journal)!;
                AtomicJson.Write(InstallationPaths.Journal, op with { Stage = UpdateStage.Accepted });
                accepted = true;
                var resume = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("resume"), check.Token);
                if (!resume.Success) throw new InvalidOperationException(resume.Message);
            }
            // Чистая установка получает настройки пользователя; повторная сохраняет историю и предпочтения.
            if (!File.Exists(Path.Combine(InstallationPaths.Updates, "state.json")))
                AtomicJson.Write(Path.Combine(InstallationPaths.Updates, "state.json"), new { current = version,
                    settings = new UpdateSettings { AutoInstall = options.GetValueOrDefault("AutoInstall", "1") == "1" } });
            else
            {
                var stateFile = Path.Combine(InstallationPaths.Updates, "state.json");
                var saved = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
                saved["current"] = version;
                if (oldVersion != version) saved["previous"] = oldVersion;
                AtomicJson.Write(stateFile, saved);
            }
            await StartAsync("Scalemon.Updater");
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Scalemon", "WebUrl", $"http://localhost:{port}");
            if (options.GetValueOrDefault("Firewall", "0") == "1") await FirewallAsync(port);
            File.WriteAllText(resultFile, "Scalemon установлен и проверен. Адрес: http://localhost:" + port);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (accepted)
                {
                    File.WriteAllText(resultFile, "Программа установлена. Требуется завершить настройку инфраструктуры: " + ex.Message);
                    return 1;
                }
                if (name is not null && stopped)
                {
                    await StopAsync(name);
                    if (backup is not null && backupComplete)
                    {
                        RestoreDirectory(backup, "Config", InstallationPaths.Config);
                        RestoreDirectory(backup, "Updates", InstallationPaths.Updates);
                        if (infrastructureReplaced) RestoreInfrastructure(backup);
                    }
                    if (oldImage is not null) { await ScAsync("config", name, "binPath=", oldImage); await StartAsync(name); }
                    else if (created) await ScAsync("delete", name);
                }
            }
            catch { /* Исходный комплект и описание службы остаются в Backups. */ }
            File.WriteAllText(resultFile, "Установка не завершена. " + ex.Message + "\r\nРезервная копия: " + backup);
            return 1;
        }
    }
    private static void RestoreDirectory(string backup, string child, string directory)
    {
        // Не удаляем неудачную конфигурацию: она остаётся для диагностики.
        if (Directory.Exists(directory))
        {
            var expected = Path.GetFullPath(Path.Combine(InstallationPaths.Root, child));
            if (!Path.GetFullPath(directory).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неверный путь восстановления.");
            Directory.Move(directory, Path.Combine(backup, "Failed-" + child));
        }
        if (Directory.Exists(Path.Combine(backup, child))) CopyTree(Path.Combine(backup, child), directory);
    }
    private static JsonObject MergeSettings(string? legacyDir)
    {
        var result = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        foreach (var path in new[] { legacyDir is null ? "" : Path.Combine(legacyDir, "appsettings.json"), @"C:\Scalemon\Scalemon.settings.json", InstallationPaths.Settings })
            if (File.Exists(path)) Merge(result, JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject());
        return result;
    }
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject child && target[pair.Key] is JsonObject existing) Merge(existing, child);
            else target[pair.Key] = pair.Value?.DeepClone();
        }
    }
    private static void MigrateLocalData(JsonObject settings, string? legacyDir, bool copyLegacyFiles)
    {
        var defaults = new[] {
            ("Authentication:UsersDatabasePath", legacyDir is null ? "" : Path.Combine(legacyDir, "users.db"), "Data/users.db"),
            ("DatabaseSettings:WeighingModeDatabasePath", @"C:\Scalemon\weighing-modes.db", "Data/weighing-modes.db"),
            ("WeightRegisterReview:StoragePath", @"C:\Scalemon\WeightRegisterReview", "Data/WeightRegisterReview"),
            ("Logging:Database:MainDatabasePath", @"C:\Logs\mainlogs.db", "Logs/mainlogs.db"),
            ("Logging:FilePath:MainLogPath", @"C:\Logs\main.log", "Logs/main.log"),
            ("Logging:FilePath:DetailedLogPath", @"C:\Logs\detailed.log", "Logs/detailed.log") };
        foreach (var (key, legacy, relative) in defaults)
        {
            var configured = Get(settings, key);
            if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathRooted(configured) && legacyDir is not null)
            { configured = Path.GetFullPath(Path.Combine(legacyDir, configured)); Set(settings, key, configured); }
            if (!string.IsNullOrEmpty(configured) && !configured.Equals(legacy, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(InstallationPaths.Root, relative);
            if (copyLegacyFiles && Directory.Exists(legacy)) CopyTree(legacy, target);
            else if (copyLegacyFiles && File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(legacy + suffix) && !File.Exists(target + suffix)) File.Copy(legacy + suffix, target + suffix);
            }
            Set(settings, key, target);
        }
        // Дневные журналы лежат рядом с главным SQLite, сохраняем весь штатный каталог.
        if (copyLegacyFiles && Get(settings, "Logging:Database:MainDatabasePath") == Path.Combine(InstallationPaths.Root, "Logs/mainlogs.db") && Directory.Exists(@"C:\Logs"))
            CopyTree(@"C:\Logs", Path.Combine(InstallationPaths.Root, "Logs"));
    }
    private static long TreeSize(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в исходной установке запрещены.");
        long size = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в исходной установке запрещены.");
            size = checked(size + (attributes.HasFlag(FileAttributes.Directory) ? TreeSize(entry) : new FileInfo(entry).Length));
        }
        return size;
    }
    private static void ReplaceInfrastructure(string source, string target)
    {
        var expected = Path.GetFullPath(Path.Combine(InstallationPaths.InstallRoot, "Infrastructure"));
        if (!Path.GetFullPath(target).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неверный путь инфраструктуры.");
        if (Directory.Exists(target)) DeleteTree(target);
        CopyTree(source, target);
    }
    private static void RestoreInfrastructure(string backup)
    {
        var target = Path.Combine(InstallationPaths.InstallRoot, "Infrastructure");
        if (Directory.Exists(target)) DeleteTree(target);
        var source = Path.Combine(backup, "Infrastructure");
        if (Directory.Exists(source)) CopyTree(source, target);
    }
    private static void DeleteTree(string directory)
    {
        EnsureNoReparse(directory);
        Directory.Delete(directory, true);
    }
    private static void EnsureNoReparse(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Каталог содержит ссылку.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Каталог содержит ссылку.");
            if (attributes.HasFlag(FileAttributes.Directory)) EnsureNoReparse(entry);
        }
    }
    private static string? Get(JsonObject root, string path)
    { JsonNode? current = root; foreach (var part in path.Split(':')) current = current?[part]; return current?.ToString(); }
    private static void Set(JsonObject root, string path, object value)
    {
        var parts = path.Split(':'); var current = root;
        foreach (var part in parts.SkipLast(1)) { current[part] ??= new JsonObject(); current = current[part]!.AsObject(); }
        current[parts[^1]] = System.Text.Json.JsonSerializer.SerializeToNode(value);
    }
    private static void CopyTree(string source, string target)
    {
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Резервная копия не может находиться внутри источника.");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Источник является ссылкой.");
        if (Directory.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Целевой каталог является ссылкой.");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в каталоге установки не поддерживаются.");
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if ((File.Exists(destination) || Directory.Exists(destination)) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Целевой путь является ссылкой.");
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(entry, destination);
            else File.Copy(entry, destination, true);
        }
    }
    private static string ImageExecutable(string image)
    {
        if (image.StartsWith('"')) return image.Split('"')[1];
        var index = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (index < 0) throw new InvalidDataException("Неизвестный путь службы.");
        return image[..(index + 4)];
    }
    private static string Quote(string value) => "\"" + value + "\"";
    private static void Protect(string directory, string? writer, string? reader = null)
    {
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var item in new[] { (writer, FileSystemRights.Modify), (reader, FileSystemRights.ReadAndExecute) })
            if (item.Item1 is not null) acl.AddAccessRule(new FileSystemAccessRule(new NTAccount("NT SERVICE", item.Item1), item.Item2,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    private static async Task FirewallAsync(int port)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "advfirewall", "firewall", "add", "rule", "name=Scalemon Web", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=private,domain" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("Не удалось настроить межсетевой экран.");
    }
    public static async Task<int> UninstallAsync()
    {
        try
        {
            var installed = AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation);
            if (installed is null) return 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
            var reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Application, new("prepare"), timeout.Token);
            if (!reply.Success) return 1;
            await StopAsync("Scalemon.Updater"); await StopAsync(installed.ServiceName);
            await ScAsync("delete", installed.ServiceName); if (Exists("Scalemon.Updater")) await ScAsync("delete", "Scalemon.Updater");
            var backup = Path.Combine(InstallationPaths.Root, "Backups", "uninstall-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backup); Protect(backup, null);
            foreach (var file in new[] { InstallationPaths.Installation, InstallationPaths.Journal, Path.Combine(InstallationPaths.Updates, "state.json") })
                if (File.Exists(file)) File.Move(file, Path.Combine(backup, Path.GetFileName(file)), true);
            await RemoveFirewallAsync();
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Scalemon", false);
            return 0;
        }
        catch { return 1; }
    }
    private static async Task RemoveFirewallAsync()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "advfirewall", "firewall", "delete", "rule", "name=Scalemon Web" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
    }
}
