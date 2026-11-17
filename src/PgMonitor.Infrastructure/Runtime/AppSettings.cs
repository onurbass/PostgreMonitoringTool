using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgMonitor.Core.Incidents;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Postgres;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.Infrastructure.Runtime;

public enum DataMode
{
    /// <summary>Fictional data from the simulator. No network, no credentials, no real mail.</summary>
    Demo,
    /// <summary>The real PostgreSQL cluster, read-only.</summary>
    Live,
}

/// <summary>
/// Everything the user can configure. Saved as JSON in the user's local app-data folder.
/// It has no password fields by design; secrets live only in Credential Manager.
/// </summary>
public sealed record AppSettings
{
    public DataMode Mode { get; init; } = DataMode.Demo;
    public PostgresConnectionSettings? Postgres { get; init; }
    public EmailSettings Email { get; init; } = new();
    public IncidentSettings Incident { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();

    /// <summary>Database name → application name shown next to it ("app_orchid_db" → "Orchid").</summary>
    public Dictionary<string, string> DatabaseApps { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Ask Windows not to sleep while the app runs on AC power (process-scoped, changes no settings).</summary>
    public bool KeepAwakeOnAcPower { get; init; }

    /// <summary>Scenario id played in demo mode.</summary>
    public string DemoScenario { get; init; } = "S05";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Mode == DataMode.Live)
        {
            if (Postgres is null || string.IsNullOrWhiteSpace(Postgres.Host))
            {
                errors.Add("PostgreSQL sunucu adı girilmeli.");
            }
            else
            {
                if (Postgres.Port is < 1 or > 65535)
                {
                    errors.Add("PostgreSQL portu 1-65535 arasında olmalı.");
                }
                if (string.IsNullOrWhiteSpace(Postgres.Username))
                {
                    errors.Add("PostgreSQL kullanıcı adı girilmeli.");
                }
            }
        }
        if (Email.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Email.SmtpHost))
            {
                errors.Add("SMTP sunucu adı girilmeli.");
            }
            if (!IsAddress(Email.From))
            {
                errors.Add("Gönderen e-posta adresi geçersiz.");
            }
            if (Email.To.Count == 0)
            {
                errors.Add("En az bir alıcı girilmeli.");
            }
            errors.AddRange(Email.To.Where(a => !IsAddress(a)).Select(a => $"Alıcı adresi geçersiz: {a}"));
        }
        if (Incident.LockWaitThreshold < TimeSpan.FromSeconds(5))
        {
            errors.Add("Lock bekleme eşiği en az 5 saniye olmalı.");
        }
        if (Storage.MaxBytes < 50L * 1024 * 1024)
        {
            errors.Add("Depolama sınırı en az 50 MB olmalı.");
        }
        return errors;
    }

    private static bool IsAddress(string? s) => !string.IsNullOrWhiteSpace(s) && MailAddress.TryCreate(s, out _);
}

/// <summary>File locations under %LOCALAPPDATA%\PgMonitor (or a given root for tests).</summary>
public sealed record AppPaths(string Root)
{
    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgMonitor"));

    public string Settings => Path.Combine(Root, "settings.json");
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Demo and live data never share a file.</summary>
    public string Database(DataMode mode) => Path.Combine(Root, mode == DataMode.Live ? "monitor.db" : "monitor-demo.db");

    public string MailDrop => Path.Combine(Root, "mail-drop");
}

public static class SettingsFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
    }

    public static void Save(string path, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);
    }
}
