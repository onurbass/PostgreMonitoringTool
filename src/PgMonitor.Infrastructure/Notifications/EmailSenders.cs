using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using PgMonitor.Infrastructure.Security;

namespace PgMonitor.Infrastructure.Notifications;

public interface IEmailSender
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);
}

/// <summary>Writes each mail as an .eml file (demo mode and tests). Never opens a network connection.</summary>
public sealed class FileDropEmailSender(string folder) : IEmailSender
{
    private int _counter;

    public string Folder { get; } = folder;

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        Directory.CreateDirectory(Folder);
        var n = Interlocked.Increment(ref _counter);
        var name = $"{message.Date.UtcDateTime:yyyyMMdd-HHmmss}-{n:000}.eml";
        await message.WriteToAsync(Path.Combine(Folder, name), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Sends through the company SMTP server with MailKit. Certificates are validated (no "trust all").</summary>
public sealed class SmtpEmailSender(EmailSettings settings, ICredentialStore credentials) : IEmailSender
{
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var client = new SmtpClient();
        client.Timeout = 30_000;
        var options = settings.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, options, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.SmtpUser))
        {
            var password = credentials.Read(CredentialTargets.Smtp(settings.SmtpUser, settings.SmtpHost))
                ?? throw new InvalidOperationException("No SMTP password saved for this account.");
            await client.AuthenticateAsync(settings.SmtpUser, password, cancellationToken).ConfigureAwait(false);
        }
        // MailKit 5 sends a SendRequest instead of a bare MimeMessage.
        await client.SendAsync(new SendRequest(message), cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }
}
