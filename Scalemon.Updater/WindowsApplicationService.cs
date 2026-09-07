using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Scalemon.Common.Updates;

namespace Scalemon.Updater;

/// <summary>Управляет только зарегистрированной службой Scalemon, без shell и принудительного завершения.</summary>
public sealed class WindowsApplicationService
{
    public string Name => AtomicJson.Read<InstallationRecord>(InstallationPaths.Installation)?.ServiceName ?? "Scalemon";
    public string ImagePath
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
            return key?.GetValue("ImagePath") as string ?? throw new InvalidOperationException("Служба Scalemon не установлена.");
        }
    }
    /// <summary>Штатно останавливает основную службу и ожидает завершения.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await ScAsync(["stop", Name], ct, allowStopped: true);
        await WaitAsync(false, ct);
    }
    /// <summary>Запускает основную службу и ожидает состояния Running.</summary>
    public async Task StartAsync(CancellationToken ct)
    { await ScAsync(["start", Name], ct, allowStopped: true); await WaitAsync(true, ct); }
    /// <summary>Переключает ImagePath на проверенный каталог указанной версии.</summary>
    public async Task SetVersionAsync(string version, CancellationToken ct)
    {
        _ = ReleaseVersion.Parse(version);
        var exe = Path.Combine(InstallationPaths.Versions, version, "Scalemon.ServiceHost.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Комплект версии отсутствует.");
        await ScAsync(["config", Name, "binPath=", "\"" + exe + "\""], ct);
    }
    /// <summary>Возвращает исходный ImagePath, сохранённый в журнале операции.</summary>
    public Task RestorePathAsync(string imagePath, CancellationToken ct)
        => ScAsync(["config", Name, "binPath=", imagePath], ct);
    private async Task WaitAsync(bool running, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            using var service = new ServiceController(Name);
            if (service.Status == (running ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped)) return;
            await Task.Delay(500, limit.Token);
        }
    }
    private static async Task<string> ScAsync(string[] args, CancellationToken ct, bool allowStopped = false)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить SCM.");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); var text = await output + await error;
        if (process.ExitCode != 0 && !(allowStopped && process.ExitCode is 1056 or 1062)) throw new IOException($"SCM: код {process.ExitCode}. {text}");
        return text;
    }
}
