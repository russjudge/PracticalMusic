using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Services;

public sealed record EmailContent(string Subject, string Text, string Html);

/// <summary>Wakes the sender as soon as something is queued.</summary>
public sealed class EmailSignal
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public void Notify() => _signal.Release();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
        while (_signal.CurrentCount > 0) _signal.Wait(0); // one pass handles everything queued so far
    }
}

/// <summary>Queues e-mail (the outbox table); <see cref="EmailSenderService"/> delivers it with retries.</summary>
public sealed class EmailService(IEmailOutboxRepository outbox, EmailSignal signal, IOptions<SiteOptions> site, ILogger<EmailService> log)
{
    public string SiteName => site.Value.Name;

    public async Task QueueAsync(string toAddress, string? toName, EmailContent content)
    {
        var now = DateTime.UtcNow;
        await outbox.EnqueueAsync(new EmailRow
        {
            ToAddress = toAddress,
            ToName = toName,
            Subject = content.Subject,
            BodyText = content.Text,
            BodyHtml = content.Html,
            NextAttemptAt = now,
            CreatedAt = now
        });
        log.LogInformation("Queued e-mail \"{Subject}\" to {To}", content.Subject, toAddress);
        signal.Notify();
    }
}

/// <summary>The messages the website sends. Every value from users is HTML-encoded.</summary>
public static class EmailTemplates
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static EmailContent Build(string site, string subject, string greetingName, IEnumerable<string> paragraphs,
        (string Text, string Url)? button = null, string? footer = null)
    {
        var paras = paragraphs.ToList();
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Hello {greetingName},").AppendLine();
        foreach (var p in paras) text.AppendLine(p).AppendLine();
        if (button is { } b) text.AppendLine($"{b.Text}: {b.Url}").AppendLine();
        if (footer != null) text.AppendLine(footer).AppendLine();
        text.AppendLine($"- {site}");

        var html = new System.Text.StringBuilder();
        html.Append("<!DOCTYPE html><html><body style=\"margin:0;padding:0;background:#f3f5f8;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1d2430\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f3f5f8;padding:24px 0\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:10px;overflow:hidden\">");
        html.Append($"<tr><td style=\"background:#0b5cad;color:#ffffff;padding:16px 24px;font-size:20px;font-weight:600\">&#9835; {H(site)}</td></tr>");
        html.Append("<tr><td style=\"padding:24px;font-size:15px;line-height:1.5\">");
        html.Append($"<p style=\"margin:0 0 14px\">Hello {H(greetingName)},</p>");
        foreach (var p in paras) html.Append($"<p style=\"margin:0 0 14px\">{H(p).Replace("\n", "<br>")}</p>");
        if (button is { } btn)
        {
            html.Append($"<p style=\"margin:22px 0\"><a href=\"{H(btn.Url)}\" style=\"background:#0b5cad;color:#ffffff;text-decoration:none;padding:11px 20px;border-radius:6px;font-weight:600;display:inline-block\">{H(btn.Text)}</a></p>");
            html.Append($"<p style=\"margin:0 0 14px;font-size:12px;color:#5b6678\">If the button does not work, copy this address into your browser:<br><span style=\"word-break:break-all\">{H(btn.Url)}</span></p>");
        }
        if (footer != null) html.Append($"<p style=\"margin:14px 0 0;font-size:13px;color:#5b6678\">{H(footer)}</p>");
        html.Append("</td></tr></table></td></tr></table></body></html>");
        return new EmailContent($"{subject} - {site}", text.ToString(), html.ToString());
    }

    public static EmailContent VerifyEmail(string site, string name, string link) => Build(site, "Confirm your e-mail address", name,
        new[] { $"Thank you for signing up for {site}. Please confirm that this is your e-mail address. The link is valid for 2 days." },
        ("Confirm my e-mail address", link),
        "If you did not sign up, you can ignore this message.");

    public static EmailContent AccountExists(string site, string name, string loginLink, string resetLink) => Build(site, "You already have an account", name,
        new[]
        {
            $"Someone (probably you) tried to sign up for {site} with this e-mail address, but an account already exists for it.",
            $"Sign in here: {loginLink}",
            "Forgot the password? You can choose a new one with the button below. To join another organization, sign in and open \"My organizations\"."
        },
        ("Reset my password", resetLink),
        "If this was not you, you can ignore this message; your account is unchanged.");

    public static EmailContent JoinRequest(string site, string managerName, string applicantName, string applicantEmail,
        string? phone, string? address, string orgName, string? message, string reviewLink)
    {
        var details = new List<string> { $"{applicantName} ({applicantEmail}) asked to join {orgName}." };
        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(phone)) contact.Add("Phone: " + phone);
        if (!string.IsNullOrWhiteSpace(address)) contact.Add("Address: " + address);
        if (contact.Count > 0) details.Add(string.Join("\n", contact));
        if (!string.IsNullOrWhiteSpace(message)) details.Add("Their message: \"" + message + "\"");
        details.Add("Approve or decline the request on the organization's Members page.");
        return Build(site, $"Request to join {orgName}", managerName, details, ("Review the request", reviewLink));
    }

    public static EmailContent AccessGranted(string site, string name, string orgName, string link) => Build(site, $"You now have access to {orgName}", name,
        new[]
        {
            $"Your request to join {orgName} was approved. You can now open its books on the website, and in the Practical Music app for Windows, Linux and Android (Online page: sign in with this e-mail address).",
            "On an iPhone or iPad, open the website in Safari and choose Share → Add to Home Screen for an app-like experience."
        },
        ("Open Practical Music", link));

    public static EmailContent AccessDeclined(string site, string name, string orgName) => Build(site, $"Your request to join {orgName}", name,
        new[] { $"Your request to join {orgName} was not approved. If you think this is a mistake, please contact the organization directly." });

    public static EmailContent PasswordReset(string site, string name, string link, int minutes) => Build(site, "Reset your password", name,
        new[] { $"A new password was requested for your {site} account. Choose it with the button below; the link works once and is valid for {minutes} minutes." },
        ("Choose a new password", link),
        "If you did not ask for this, ignore this message: your password stays the same.");

    public static EmailContent Invitation(string site, string name, string orgName, string inviterName, string link, int days) => Build(site, $"You are invited to {orgName}", name,
        new[]
        {
            $"{inviterName} added you to {orgName} on {site}, where the organization keeps its sheet music and recordings.",
            $"Choose a password to activate your account (the link is valid for {days} days). Your user name is this e-mail address."
        },
        ("Choose my password", link));

    public static EmailContent InvitationToJoin(string site, string name, string orgName, string inviterName, string link) => Build(site, $"Invitation to {orgName}", name,
        new[]
        {
            $"{inviterName} invited you to join {orgName} on {site}, where the organization keeps its sheet music and recordings.",
            "Sign in and accept the invitation under \"My organizations\". If you do not want to join, decline it there or simply ignore this message."
        },
        ("See the invitation", link));

    public static EmailContent AddedToOrganization(string site, string name, string orgName, string inviterName, string link) => Build(site, $"You now have access to {orgName}", name,
        new[] { $"{inviterName} gave you access to {orgName}. Its books are now available when you sign in." },
        ("Open Practical Music", link));

    public static EmailContent PasswordChanged(string site, string name, string resetLink) => Build(site, "Your password was changed", name,
        new[] { "The password of your account was just changed. If you did this, there is nothing else to do." },
        ("I did not do this - reset my password", resetLink));

    public static EmailContent Test(string site, string sentBy) => Build(site, "Test message", sentBy,
        new[] { $"This is a test message from {site}. If you can read it, outgoing e-mail works." });
}

/// <summary>How messages leave the website.</summary>
public interface IMailTransport
{
    Task SendAsync(EmailRow email, CancellationToken ct);
    string Describe();
}

public static class MailMessages
{
    public static MimeMessage Build(EmailRow email, EmailOptions options)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        if (!string.IsNullOrWhiteSpace(options.ReplyTo)) message.ReplyTo.Add(MailboxAddress.Parse(options.ReplyTo));
        message.To.Add(new MailboxAddress(email.ToName ?? "", email.ToAddress));
        message.Subject = email.Subject;
        message.Headers.Add("Auto-Submitted", "auto-generated"); // RFC 3834: no auto-replies, please
        var body = new BodyBuilder { TextBody = email.BodyText };
        if (!string.IsNullOrEmpty(email.BodyHtml)) body.HtmlBody = email.BodyHtml;
        message.Body = body.ToMessageBody();
        return message;
    }
}

public sealed class SmtpMailTransport(IOptions<EmailOptions> options) : IMailTransport
{
    public async Task SendAsync(EmailRow email, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost)) throw new InvalidOperationException("Email:SmtpHost is not configured.");
        using var client = new SmtpClient { Timeout = 30_000 };
        var security = o.SmtpSecurity.ToLowerInvariant() switch
        {
            "sslonconnect" or "ssl" or "tls" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            "auto" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.StartTls
        };
        await client.ConnectAsync(o.SmtpHost, o.SmtpPort, security, ct);
        if (!string.IsNullOrEmpty(o.SmtpUser)) await client.AuthenticateAsync(o.SmtpUser, o.SmtpPassword ?? "", ct);
        await client.SendAsync(MailMessages.Build(email, o), ct);
        await client.DisconnectAsync(true, ct);
    }

    public string Describe()
    {
        var o = options.Value;
        return $"SMTP {o.SmtpHost}:{o.SmtpPort} ({o.SmtpSecurity}{(string.IsNullOrEmpty(o.SmtpUser) ? ", no login" : ", user " + o.SmtpUser)}) from {o.FromAddress}";
    }
}

/// <summary>Writes each message as an .eml file instead of sending it (testing, or while e-mail is being set up).</summary>
public sealed class PickupMailTransport(IOptions<EmailOptions> options, DataPaths paths) : IMailTransport
{
    public string Directory => string.IsNullOrWhiteSpace(options.Value.PickupDirectory)
        ? Path.Combine(paths.Root, "mail-pickup")
        : Path.GetFullPath(options.Value.PickupDirectory);

    public async Task SendAsync(EmailRow email, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = Path.Combine(Directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{email.Id}-{Guid.NewGuid().ToString("N")[..6]}.eml");
        await MailMessages.Build(email, options.Value).WriteToAsync(file, ct);
    }

    public string Describe() => $"written as .eml files to {Directory} (Email:Mode = Pickup; nothing is actually sent)";
}

public sealed class DisabledMailTransport : IMailTransport
{
    public Task SendAsync(EmailRow email, CancellationToken ct) =>
        throw new InvalidOperationException("Sending e-mail is turned off (Email:Mode = Disabled).");

    public string Describe() => "turned off (Email:Mode = Disabled)";
}

/// <summary>Delivers queued e-mail. Failed attempts are retried after 1, 5, 15, 60, 240 and 720 minutes, then given up (the administrator can retry).</summary>
public sealed class EmailSenderService(IServiceScopeFactory scopes, IMailTransport transport, EmailSignal signal, ILogger<EmailSenderService> log)
    : BackgroundService
{
    private static readonly int[] RetryMinutes = { 1, 5, 15, 60, 240, 720 };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("E-mail: {Transport}", transport.Describe());
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SendDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "E-mail outbox could not be processed"); }
            await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task SendDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutboxRepository>();
        foreach (var email in await outbox.FetchDueAsync(DateTime.UtcNow, 20))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await transport.SendAsync(email, ct);
                await outbox.MarkSentAsync(email.Id, DateTime.UtcNow);
                log.LogInformation("Sent e-mail {Id} \"{Subject}\" to {To}", email.Id, email.Subject, email.ToAddress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var attempts = email.Attempts + 1;
                var giveUp = attempts > RetryMinutes.Length;
                var next = DateTime.UtcNow.AddMinutes(RetryMinutes[Math.Min(attempts, RetryMinutes.Length) - 1]);
                await outbox.MarkAttemptFailedAsync(email.Id, attempts, next, ex.Message, giveUp);
                log.LogWarning("E-mail {Id} to {To} failed (attempt {Attempt}{GiveUp}): {Error}", email.Id, email.ToAddress, attempts,
                    giveUp ? ", giving up" : "", ex.Message);
            }
        }
    }
}
