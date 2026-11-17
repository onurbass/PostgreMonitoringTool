using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Npgsql;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Postgres;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Security;

namespace PgMonitor.App.ViewModels;

/// <summary>
/// Editable copy of <see cref="AppSettings"/>. Passwords are never held here: the view passes them straight from
/// the PasswordBox to the credential store.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _isDemo = true;
    [ObservableProperty] private string _demoScenario = "S05";

    [ObservableProperty] private string _pgHost = "";
    [ObservableProperty] private string _pgPort = "5432";
    [ObservableProperty] private string _pgDatabase = "postgres";
    [ObservableProperty] private string _pgUser = "";
    [ObservableProperty] private SslMode _pgSsl = SslMode.Prefer;
    [ObservableProperty] private string _pgPasswordState = "";

    [ObservableProperty] private bool _mailEnabled;
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private string _smtpPort = "587";
    [ObservableProperty] private SmtpSecurity _smtpSecurity = SmtpSecurity.StartTls;
    [ObservableProperty] private string _smtpUser = "";
    [ObservableProperty] private string _mailFrom = "";
    [ObservableProperty] private string _mailTo = "";
    [ObservableProperty] private string _sqlMaxChars = "500";
    [ObservableProperty] private bool _sendOnEscalation = true;
    [ObservableProperty] private string _smtpPasswordState = "";

    [ObservableProperty] private string _normalInterval = "10";
    [ObservableProperty] private string _incidentInterval = "5";
    [ObservableProperty] private string _lockWaitSeconds = "30";
    [ObservableProperty] private string _longTxMinutes = "10";
    [ObservableProperty] private string _recordLockWaitSeconds = "5";
    [ObservableProperty] private string _highWaiters = "5";
    [ObservableProperty] private string _criticalWaiters = "20";
    [ObservableProperty] private string _maxStorageMb = "1024";
    [ObservableProperty] private string _incidentRetentionDays = "180";
    [ObservableProperty] private string _databaseApps = "";
    [ObservableProperty] private bool _keepAwake;

    [ObservableProperty] private string _message = "";

    /// <summary>Second radio button: the inverse of <see cref="IsDemo"/>.</summary>
    public bool IsLive
    {
        get => !IsDemo;
        set => IsDemo = !value;
    }

    partial void OnIsDemoChanged(bool value) => OnPropertyChanged(nameof(IsLive));

    public IReadOnlyList<string> ScenarioIds { get; } = Scenarios.All.Select(s => s.Id).ToList();
    public IReadOnlyList<SslMode> SslModes { get; } = [SslMode.Disable, SslMode.Prefer, SslMode.Require, SslMode.VerifyCA, SslMode.VerifyFull];
    public IReadOnlyList<SmtpSecurity> SmtpSecurities { get; } = Enum.GetValues<SmtpSecurity>();

    public void Load(AppSettings s, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(credentials);
        IsDemo = s.Mode == DataMode.Demo;
        DemoScenario = s.DemoScenario;
        if (s.Postgres is { } pg)
        {
            PgHost = pg.Host;
            PgPort = pg.Port.ToString(CultureInfo.InvariantCulture);
            PgDatabase = pg.Database;
            PgUser = pg.Username;
            PgSsl = pg.SslMode;
        }
        var e = s.Email;
        MailEnabled = e.Enabled;
        SmtpHost = e.SmtpHost;
        SmtpPort = e.SmtpPort.ToString(CultureInfo.InvariantCulture);
        SmtpSecurity = e.Security;
        SmtpUser = e.SmtpUser;
        MailFrom = e.From;
        MailTo = string.Join("; ", e.To);
        SqlMaxChars = e.SqlMaxChars.ToString(CultureInfo.InvariantCulture);
        SendOnEscalation = e.SendOnEscalation;
        NormalInterval = s.Sampling.NormalIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        IncidentInterval = s.Sampling.IncidentIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        LockWaitSeconds = ((int)s.Incident.LockWaitThreshold.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        LongTxMinutes = ((int)s.Incident.LongTransactionThreshold.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        RecordLockWaitSeconds = ((int)s.Incident.RecordLockWaitsFrom.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        HighWaiters = s.Incident.HighWaiterCount.ToString(CultureInfo.InvariantCulture);
        CriticalWaiters = s.Incident.CriticalWaiterCount.ToString(CultureInfo.InvariantCulture);
        MaxStorageMb = (s.Storage.MaxBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture);
        IncidentRetentionDays = ((int)s.Storage.IncidentRetention.TotalDays).ToString(CultureInfo.InvariantCulture);
        DatabaseApps = string.Join(Environment.NewLine, s.DatabaseApps.Select(kv => $"{kv.Key}={kv.Value}"));
        KeepAwake = s.KeepAwakeOnAcPower;
        RefreshPasswordStates(credentials);
    }

    public void RefreshPasswordStates(ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        PgPasswordState = PgTarget() is { } t && credentials.Read(t) is not null ? "Şifre kayıtlı." : "Şifre kayıtlı değil.";
        SmtpPasswordState = string.IsNullOrWhiteSpace(SmtpUser) ? "Kimlik doğrulama yok."
            : credentials.Read(CredentialTargets.Smtp(SmtpUser.Trim(), SmtpHost.Trim())) is not null ? "Şifre kayıtlı." : "Şifre kayıtlı değil.";
    }

    public string? PgTarget() =>
        string.IsNullOrWhiteSpace(PgHost) || string.IsNullOrWhiteSpace(PgUser) || !int.TryParse(PgPort, out var port)
            ? null
            : CredentialTargets.Postgres(PgUser.Trim(), PgHost.Trim(), port);

    public string? SmtpTarget() =>
        string.IsNullOrWhiteSpace(SmtpUser) || string.IsNullOrWhiteSpace(SmtpHost) ? null : CredentialTargets.Smtp(SmtpUser.Trim(), SmtpHost.Trim());

    public PostgresConnectionSettings? BuildPostgres() =>
        string.IsNullOrWhiteSpace(PgHost) && string.IsNullOrWhiteSpace(PgUser)
            ? null
            : new PostgresConnectionSettings
            {
                Host = PgHost.Trim(),
                Port = Int(PgPort, 0),
                Database = string.IsNullOrWhiteSpace(PgDatabase) ? "postgres" : PgDatabase.Trim(),
                Username = PgUser.Trim(),
                SslMode = PgSsl,
            };

    public EmailSettings BuildEmail(EmailSettings current) => current with
    {
        Enabled = MailEnabled,
        SmtpHost = SmtpHost.Trim(),
        SmtpPort = Int(SmtpPort, 0),
        Security = SmtpSecurity,
        SmtpUser = SmtpUser.Trim(),
        From = MailFrom.Trim(),
        To = MailTo.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        SqlMaxChars = Int(SqlMaxChars, 500),
        SendOnEscalation = SendOnEscalation,
    };

    public AppSettings Build(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var apps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in DatabaseApps.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i > 0)
            {
                apps[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }
        return current with
        {
            Mode = IsDemo ? DataMode.Demo : DataMode.Live,
            DemoScenario = DemoScenario,
            Postgres = BuildPostgres(),
            Email = BuildEmail(current.Email),
            Incident = current.Incident with
            {
                LockWaitThreshold = TimeSpan.FromSeconds(Int(LockWaitSeconds, 30)),
                HighWaiterCount = Int(HighWaiters, 5),
                CriticalWaiterCount = Int(CriticalWaiters, 20),
                LongTransactionThreshold = TimeSpan.FromMinutes(Int(LongTxMinutes, 10)),
                RecordLockWaitsFrom = TimeSpan.FromSeconds(Int(RecordLockWaitSeconds, 5)),
            },
            Storage = current.Storage with
            {
                MaxBytes = Int(MaxStorageMb, 1024) * 1024L * 1024L,
                IncidentRetention = TimeSpan.FromDays(Int(IncidentRetentionDays, 180)),
            },
            DatabaseApps = apps,
            KeepAwakeOnAcPower = KeepAwake,
            Sampling = new SamplingSettings
            {
                NormalIntervalSeconds = Int(NormalInterval, 10),
                IncidentIntervalSeconds = Int(IncidentInterval, 5),
            },
        };
    }

    private static int Int(string s, int fallback) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
