using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PgMonitor.App.Services;
using PgMonitor.App.ViewModels;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Security;
using Serilog;
using Serilog.Extensions.Logging;
using WinForms = System.Windows.Forms;

namespace PgMonitor.App;

/// <summary>
/// Composition root. Options:
///   --data &lt;folder&gt;      use another data folder instead of %LOCALAPPDATA%\PgMonitor
///   --snapshot &lt;folder&gt;  self-check: run the demo for --seconds (default 40), save one PNG per tab, exit
/// </summary>
#pragma warning disable CA1001 // WPF Application has no Dispose; everything is disposed in ExitAsync.
public partial class App : System.Windows.Application
#pragma warning restore CA1001
{
    private Mutex? _singleInstance;
    private WinForms.NotifyIcon? _tray;
    private MonitorService? _service;
    private MainWindow? _window;
    private Serilog.Core.Logger? _log;
    private SerilogLoggerFactory? _loggers;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        var args = e.Args;
        var snapshotDir = Arg(args, "--snapshot");
        var dataDir = Arg(args, "--data");
        var paths = dataDir is null ? AppPaths.Default : new AppPaths(Path.GetFullPath(dataDir));

        if (snapshotDir is null)
        {
            _singleInstance = new Mutex(true, @"Local\PgMonitor.SingleInstance", out var first);
            if (!first)
            {
                System.Windows.MessageBox.Show("PgMonitor zaten çalışıyor (sistem tepsisine bakın).", "PgMonitor");
                Shutdown();
                return;
            }
        }

        Directory.CreateDirectory(paths.Logs);
        _log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.Logs, "pgmonitor-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(_log);
        var logger = _loggers.CreateLogger<App>();
        DispatcherUnhandledException += (_, ex) =>
        {
            logger.LogError("Unhandled UI error: {Type}", ex.Exception.GetType().Name);
            ex.Handled = true;
        };

        var settings = SettingsFile.Load(paths.Settings);
        ICredentialStore saved = OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new InMemoryCredentialStore();
        var credentials = new LayeredCredentialStore(new InMemoryCredentialStore(), saved);

        if (snapshotDir is not null)
        {
            settings = settings with { Mode = DataMode.Demo };
        }
        _service = new MonitorService(paths, credentials, _loggers, snapshotDir is null ? null : TimeSpan.FromMilliseconds(40));
        var vm = new MainViewModel(_service);
        _window = new MainWindow(vm);

        if (snapshotDir is not null)
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = -20000; // render off-screen, never on the user's desktop
            _window.Top = -20000;
            _window.ShowInTaskbar = false;
            _window.Show();
            await vm.StartAsync(settings).ConfigureAwait(true);
            var seconds = int.TryParse(Arg(args, "--seconds"), out var s) ? s : 40;
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(true);
            _window.SaveTabSnapshots(Path.GetFullPath(snapshotDir));
            await ExitAsync().ConfigureAwait(true);
            return;
        }

        CreateTray(vm);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _window.Show();
        await vm.StartAsync(settings).ConfigureAwait(true);
        _log.Information("PgMonitor started in {Mode} mode", settings.Mode);
    }

    private void CreateTray(MainViewModel vm)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => ShowWindow());
        menu.Items.Add("Çıkış (izlemeyi durdur)", null, async (_, _) => await ExitAsync().ConfigureAwait(true));
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "PgMonitor – PostgreSQL lock izleme",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        vm.IncidentOpened += (_, text) =>
            _tray?.ShowBalloonTip(10_000, vm.IsSimulated ? "PgMonitor (SİMÜLASYON)" : "PgMonitor – lock olayı", text, WinForms.ToolTipIcon.Warning);
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => PowerHelper.Refresh();

    private async Task ExitAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_service is not null)
        {
            await _service.DisposeAsync().ConfigureAwait(true);
        }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _loggers?.Dispose();
        _log?.Dispose();
        _singleInstance?.Dispose();
        Shutdown();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
