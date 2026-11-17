using System.Globalization;
using Npgsql;

namespace PgMonitor.Infrastructure.Postgres;

/// <summary>
/// Non-secret connection settings (may be stored in the local settings file). The password is never part of this
/// record; it is supplied at connect time from Windows Credential Manager or from memory.
/// </summary>
public sealed record PostgresConnectionSettings
{
    public required string Host { get; init; }
    public int Port { get; init; } = 5432;

    /// <summary>Any database the account may connect to; pg_stat_activity shows the whole cluster from any of them.</summary>
    public string Database { get; init; } = "postgres";

    public required string Username { get; init; }
    public SslMode SslMode { get; init; } = SslMode.Prefer;
    public int ConnectTimeoutSeconds { get; init; } = 5;

    /// <summary>Client-side command timeout. Keep below the sampling interval.</summary>
    public int CommandTimeoutSeconds { get; init; } = 4;

    /// <summary>Server-side statement_timeout, so a slow catalog query can never pile up on the server.</summary>
    public int StatementTimeoutMs { get; init; } = 3000;

    /// <summary>Shown in pg_stat_activity so the DB team can recognise this monitor.</summary>
    public const string ApplicationName = "PgMonitor (read-only)";

    /// <summary>Builds the Npgsql connection string. The result contains the password: never log it.</summary>
    internal string BuildConnectionString(string? password)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = password,
            SslMode = SslMode,
            Timeout = ConnectTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            ApplicationName = ApplicationName,
            // Read-only for every transaction of this session, plus a server-side time limit (TECHNICAL_DECISIONS D4).
            Options = string.Create(CultureInfo.InvariantCulture,
                $"-c default_transaction_read_only=on -c statement_timeout={StatementTimeoutMs}"),
            KeepAlive = 30,
            Pooling = true,
            MaxPoolSize = 2,
            IncludeErrorDetail = false,
            PersistSecurityInfo = false,
        };
        return b.ConnectionString;
    }

    /// <summary>Safe for logs and UI: no password.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Username}@{Host}:{Port}/{Database} (ssl={SslMode})");

    public override string ToString() => Describe();
}
