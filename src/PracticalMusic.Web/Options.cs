namespace PracticalMusic.Web;

/// <summary>"Site" section of the configuration.</summary>
public sealed class SiteOptions
{
    public const string Section = "Site";

    public string Name { get; set; } = "Practical Music";

    /// <summary>
    /// Public address of this website, for example https://practicalmusic.russjudge.com. Used in links inside
    /// e-mails and as the "server" of downloaded books. When empty it is taken from the incoming request.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Where uploaded media, the page-image cache, keys and temporary files live. Relative paths are relative to the content root.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>New accounts must confirm their e-mail address before they can sign in.</summary>
    public bool RequireEmailVerification { get; set; } = true;

    /// <summary>Largest accepted upload (a whole .pmbook with its PDFs and recordings).</summary>
    public int MaxUploadMegabytes { get; set; } = 600;

    /// <summary>Rendered page images are cached on disk up to this size; the least recently used are removed first.</summary>
    public int PageCacheMegabytes { get; set; } = 2048;

    /// <summary>The Downloads page (apps for Windows, Linux and Android placed in &lt;DataDir&gt;/downloads) is visible without signing in.</summary>
    public bool PublicDownloads { get; set; } = true;

    /// <summary>Shown to users who need help (optional).</summary>
    public string? SupportEmail { get; set; }

    /// <summary>How long an app (desktop/Android) stays signed in.</summary>
    public int ApiTokenDays { get; set; } = 365;

    /// <summary>Sign-in, sign-up and password e-mail requests allowed per minute from one address (brute-force protection).</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;
}

/// <summary>"Email" section of the configuration.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Smtp = send through an SMTP server; Pickup = write .eml files to <see cref="PickupDirectory"/> (testing); Disabled = do not send.</summary>
    public string Mode { get; set; } = "Pickup";

    public string FromAddress { get; set; } = "noreply@example.com";
    public string FromName { get; set; } = "Practical Music";
    public string? ReplyTo { get; set; }

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    /// <summary>StartTls (port 587), SslOnConnect (port 465), Auto, or None (only for a relay on localhost).</summary>
    public string SmtpSecurity { get; set; } = "StartTls";
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }

    /// <summary>For Mode = Pickup. Default: &lt;DataDir&gt;/mail-pickup.</summary>
    public string? PickupDirectory { get; set; }

    public bool IsSmtp => Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase);
    public bool IsPickup => Mode.Equals("Pickup", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => !IsSmtp && !IsPickup;
}

/// <summary>"Database" section of the configuration.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>MariaDb (default; also works with MySQL 8) or Memory (development and automated tests only - nothing is kept after a restart).</summary>
    public string Provider { get; set; } = "MariaDb";

    public bool IsMemory => Provider.Equals("Memory", StringComparison.OrdinalIgnoreCase);
}
