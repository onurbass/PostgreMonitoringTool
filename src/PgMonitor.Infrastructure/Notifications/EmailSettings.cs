namespace PgMonitor.Infrastructure.Notifications;

public enum SmtpSecurity
{
    /// <summary>Connect in plain text, then upgrade with STARTTLS (required). Typical port 587.</summary>
    StartTls,
    /// <summary>TLS from the first byte. Typical port 465.</summary>
    SslOnConnect,
    /// <summary>No TLS. Only for an internal relay that does not support it.</summary>
    None,
}

/// <summary>Non-secret e-mail settings. The SMTP password lives in Credential Manager (target from <see cref="Security.CredentialTargets.Smtp"/>).</summary>
public sealed record EmailSettings
{
    public bool Enabled { get; init; }
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    /// <summary>Empty when the relay accepts mail without authentication.</summary>
    public string SmtpUser { get; init; } = "";

    public string From { get; init; } = "";
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Characters of SQL text per session in mails; 0 = no SQL in mails (TECHNICAL_DECISIONS D9).</summary>
    public int SqlMaxChars { get; init; } = 500;

    public bool SendOnEscalation { get; init; } = true;

    /// <summary>At most one escalation mail per incident in this period. Opened and Closed are always sent.</summary>
    public TimeSpan EscalationCooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Retry failed sends with growing delays for this long, then give up.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromHours(24);

    public int MaxSuspectsInMail { get; init; } = 5;
}
