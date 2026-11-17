using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgMonitor.App.Services;
using PgMonitor.Infrastructure.Runtime;

namespace PgMonitor.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly MonitorService _service;
    private long _lastIncidentRefreshId = -1;
    private int _lastLongTxCount = -1;
    private DateTimeOffset _lastStorageCheck = DateTimeOffset.MinValue;
    private string _storageLine = "-";

    [ObservableProperty] private bool _isSimulated = true;
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _busy;

    public MainViewModel(MonitorService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        Dashboard = new DashboardViewModel();
        Incidents = new IncidentsViewModel(() => _service.Store, () => _service.Settings.DatabaseApps);
        Settings = new SettingsViewModel();
        LongTransactions = new LongTransactionsViewModel(() => _service.Store, () => _service.Settings.DatabaseApps);
        _service.SnapshotReceived += OnSnapshot;
    }

    public DashboardViewModel Dashboard { get; }
    public IncidentsViewModel Incidents { get; }
    public LongTransactionsViewModel LongTransactions { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Raised on the UI thread when an incident opens (for the tray balloon).</summary>
    public event EventHandler<string>? IncidentOpened;

    public async Task StartAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings.Load(settings, _service.Credentials);
        await ApplyAsync(settings).ConfigureAwait(true);
    }

    private async Task ApplyAsync(AppSettings settings)
    {
        Busy = true;
        try
        {
            await _service.StartAsync(settings).ConfigureAwait(true);
            IsSimulated = settings.Mode == DataMode.Demo;
            ModeText = IsSimulated
                ? $"DEMO – senaryo {settings.DemoScenario} (kurgusal veri, 10× hızlı)"
                : $"CANLI – {settings.Postgres?.Describe()} (salt okunur) – {settings.Sampling.NormalIntervalSeconds} sn'de bir, olayda {settings.Sampling.IncidentIntervalSeconds} sn";
            _lastIncidentRefreshId = -1;
            _lastLongTxCount = -1;
            Incidents.Refresh();
            LongTransactions.Refresh();
            PowerHelper.KeepAwake(settings.KeepAwakeOnAcPower);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Settings.Message = "Başlatılamadı: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnSnapshot(object? sender, MonitorSnapshot s)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }
        app.Dispatcher.BeginInvoke(() =>
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastStorageCheck > TimeSpan.FromSeconds(2) && _service.Store is { } store)
            {
                _lastStorageCheck = now;
                var r = store.Report();
                _storageLine = string.Create(CultureInfo.InvariantCulture,
                    $"{(r.TotalBytes / 1024.0 / 1024.0):0.0} MB (WAL {(r.WalBytes / 1024.0 / 1024.0):0.0} MB) • {r.IncidentCount} olay • {r.AggregateRows} dakika özeti");
            }
            Dashboard.Apply(s, _service.Settings.DatabaseApps, _storageLine);

            var incidentId = s.State.Incident?.Id ?? 0;
            var changed = s.RecentEvents.Count > 0 && s.RecentEvents[^1].At >= (s.Sample?.SampledAt ?? DateTimeOffset.MaxValue);
            if (incidentId != _lastIncidentRefreshId || changed)
            {
                _lastIncidentRefreshId = incidentId;
                Incidents.Refresh();
            }
            if (s.LongTransactions.Count != _lastLongTxCount)
            {
                _lastLongTxCount = s.LongTransactions.Count;
                LongTransactions.Refresh(); // a long transaction started or ended
            }
            foreach (var e in s.RecentEvents.Where(e => e.At == s.Sample?.SampledAt &&
                                                        e.Kind is Core.Incidents.IncidentEventKind.Opened or Core.Incidents.IncidentEventKind.Reopened))
            {
                IncidentOpened?.Invoke(this, $"Olay #{e.IncidentId}: {e.Detail}");
            }
        });
    }

    // ------------------------------------------------------------------ settings commands

    [RelayCommand]
    private async Task SaveAndApply()
    {
        var next = Settings.Build(_service.Settings);
        var errors = next.Validate();
        if (errors.Count > 0)
        {
            Settings.Message = "Kaydedilmedi:\n• " + string.Join("\n• ", errors);
            return;
        }
        if (next.Mode == DataMode.Live && Settings.PgTarget() is { } t && _service.Credentials.Read(t) is null)
        {
            Settings.Message = "Uyarı: bu bağlantı için şifre yok. Önce şifreyi kaydedin (veya 'sadece bu oturum').";
        }
        SettingsFile.Save(_service.Paths.Settings, next);
        await ApplyAsync(next).ConfigureAwait(true);
        if (string.IsNullOrEmpty(Settings.Message) || Settings.Message.StartsWith("Kaydedildi", StringComparison.Ordinal))
        {
            Settings.Message = $"Kaydedildi ve uygulandı ({DateTime.Now:HH:mm:ss}).";
        }
    }

    public void SavePostgresPassword(string password, bool persist)
    {
        if (Settings.PgTarget() is not { } target || string.IsNullOrEmpty(password))
        {
            Settings.Message = "Sunucu, port, kullanıcı ve şifre girilmeli.";
            return;
        }
        var store = persist ? _service.Credentials.Saved : _service.Credentials.Session;
        store.Write(target, Settings.PgUser.Trim(), password);
        Settings.RefreshPasswordStates(_service.Credentials);
        Settings.Message = persist ? "PostgreSQL şifresi Windows Credential Manager'a kaydedildi." : "PostgreSQL şifresi sadece bu oturum için tutuluyor.";
    }

    public void DeletePostgresPassword()
    {
        if (Settings.PgTarget() is { } target)
        {
            _service.Credentials.Delete(target);
        }
        Settings.RefreshPasswordStates(_service.Credentials);
        Settings.Message = "PostgreSQL şifresi silindi.";
    }

    public void SaveSmtpPassword(string password, bool persist)
    {
        if (Settings.SmtpTarget() is not { } target || string.IsNullOrEmpty(password))
        {
            Settings.Message = "SMTP sunucusu, kullanıcı ve şifre girilmeli.";
            return;
        }
        var store = persist ? _service.Credentials.Saved : _service.Credentials.Session;
        store.Write(target, Settings.SmtpUser.Trim(), password);
        Settings.RefreshPasswordStates(_service.Credentials);
        Settings.Message = persist ? "SMTP şifresi Windows Credential Manager'a kaydedildi." : "SMTP şifresi sadece bu oturum için tutuluyor.";
    }

    public void DeleteSmtpPassword()
    {
        if (Settings.SmtpTarget() is { } target)
        {
            _service.Credentials.Delete(target);
        }
        Settings.RefreshPasswordStates(_service.Credentials);
        Settings.Message = "SMTP şifresi silindi.";
    }

    public async Task TestConnectionAsync(string? typedPassword)
    {
        var pg = Settings.BuildPostgres();
        if (pg is null || string.IsNullOrWhiteSpace(pg.Host) || string.IsNullOrWhiteSpace(pg.Username) || pg.Port is < 1 or > 65535)
        {
            Settings.Message = "Sunucu, port ve kullanıcı girilmeli.";
            return;
        }
        Busy = true;
        Settings.Message = "Bağlanılıyor…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _service.TestConnectionAsync(pg, string.IsNullOrEmpty(typedPassword) ? null : typedPassword, cts.Token).ConfigureAwait(true);
            Settings.Message = result.Summary;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SendTestMailAsync()
    {
        var email = Settings.BuildEmail(_service.Settings.Email) with { Enabled = true };
        var errors = (_service.Settings with { Email = email }).Validate().Where(e => !e.Contains("PostgreSQL", StringComparison.Ordinal)).ToList();
        if (errors.Count > 0)
        {
            Settings.Message = "Test maili gönderilemedi:\n• " + string.Join("\n• ", errors);
            return;
        }
        Busy = true;
        Settings.Message = "Test maili gönderiliyor…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _service.SendTestMailAsync(email, cts.Token).ConfigureAwait(true);
            Settings.Message = $"Test maili gönderildi: {string.Join(", ", email.To)}";
        }
#pragma warning disable CA1031 // Shown to the user as a type name only; never crash the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Settings.Message = $"Test maili gönderilemedi: {ex.GetType().Name}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_service.Paths.Root);

    [RelayCommand]
    private void OpenMailFolder() => OpenFolder(_service.Paths.MailDrop);

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
