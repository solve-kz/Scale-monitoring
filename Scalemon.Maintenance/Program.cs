using Scalemon.Common.Updates;

namespace Scalemon.Maintenance;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--open" or "--display")
        {
            var url = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Scalemon", "WebUrl", null) as string;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString().TrimEnd('/') + (args[0] == "--display" ? "/production-display" : "/")) { UseShellExecute = true });
            return;
        }
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            try { System.Diagnostics.Process.Start(start); } catch { }
            return;
        }
        if (args.Length == 2 && args[0] == "--discover")
        { SetupOperations.Discover(args[1]); return; }
        if (args.Length == 3 && args[0] == "--setup")
        { Environment.ExitCode = SetupOperations.InstallAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
        if (args.Length == 1 && args[0] == "--uninstall")
        { Environment.ExitCode = SetupOperations.UninstallAsync().GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize(); Application.Run(new MaintenanceForm());
    }
}

internal sealed class MaintenanceForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Top, Height = 85, AutoSize = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private bool _busy;
    public MaintenanceForm()
    {
        Text = "Обслуживание Scalemon"; Width = 920; Height = 640; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_text); Controls.Add(_buttons);
        Add("Обновить состояние", "status"); Add("Проверить GitHub", "check");
        Add("Отменить ожидание", "cancel"); Add("Вернуть предыдущую версию", "rollback");
        Add("Восстановить прерванную операцию", "recover");
        _timer.Tick += async (_, _) => { if (!_busy) await SendAsync("status"); };
        Shown += async (_, _) => { await SendAsync("status"); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }
    private void Add(string title, string command)
    {
        var button = new Button { Text = title, AutoSize = true, Height = 32 };
        button.Click += async (_, _) => await SendAsync(command); _buttons.Controls.Add(button);
    }
    private async Task SendAsync(string command)
    {
        if (_busy) return; _busy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await LocalUpdatePipe.CallAsync(LocalUpdatePipe.Updater, new(command), timeout.Token);
            if (reply.Status is { } s)
                _text.Text = $"Установлена: {s.Current}\r\nПредыдущая: {s.Previous ?? "нет"}\r\n{s.Message}\r\n{reply.Message}\r\n\r\n" + string.Join("\r\n", s.History.Reverse());
            else _text.Text = reply.Message;
        }
        catch (Exception ex) { _text.Text = "Не удалось связаться со службой обновлений. Проверьте Scalemon.Updater в службах Windows.\r\n" + ex.Message; }
        finally { _busy = false; }
    }
}
