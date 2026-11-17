using Microsoft.Extensions.Options;
using PracticalMusic.Core.Pdf;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web;

/// <summary>
/// Administration from the server's command line, e.g.
/// <c>sudo -u practicalmusic /opt/practicalmusic/PracticalMusic.Web create-admin --email you@example.com --name "Your Name"</c>
/// (run as the service user, so the same configuration file is used).
/// </summary>
public static class Cli
{
    private static readonly string[] Commands = { "create-admin", "check", "send-test-email", "password-link", "help", "--help" };

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "create-admin": return await CreateAdminAsync(sp, args);
                case "check": return await CheckAsync(sp);
                case "send-test-email": return await SendTestEmailAsync(sp, args);
                case "password-link": return await PasswordLinkAsync(sp, args);
                default: Help(); return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static void Help()
    {
        Console.WriteLine("""
            Practical Music website - command line

              PracticalMusic.Web                                   run the website
              PracticalMusic.Web create-admin --email E --name N   create (or promote) the website administrator;
                                                                   asks for a password (or --password-stdin)
              PracticalMusic.Web check                             check configuration, database, storage, PDF engine, e-mail
              PracticalMusic.Web send-test-email --to E            send a test message now and show the result
              PracticalMusic.Web password-link --email E           print a one-time link to set a password (when e-mail
                                                                   does not work yet)

            Configuration: appsettings.json next to the program, then /etc/practicalmusic/appsettings.json
            (or the file named in PRACTICALMUSIC_CONFIG), then environment variables PRACTICALMUSIC_Section__Key.
            """);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task<int> CreateAdminAsync(IServiceProvider sp, string[] args)
    {
        var email = Text.NormalizeEmail(Option(args, "email"));
        if (!Text.IsValidEmail(email)) { Console.Error.WriteLine("Use: create-admin --email you@example.com --name \"Your Name\""); return 2; }
        var users = sp.GetRequiredService<IUserRepository>();
        var hashing = sp.GetRequiredService<PasswordHashing>();
        var existing = await users.FindByEmailAsync(email);
        var now = DateTime.UtcNow;

        string? password = null;
        if (existing == null || args.Contains("--reset-password", StringComparer.OrdinalIgnoreCase) || args.Contains("--password-stdin", StringComparer.OrdinalIgnoreCase))
        {
            password = args.Contains("--password-stdin", StringComparer.OrdinalIgnoreCase) ? Console.In.ReadLine() : AskPassword();
            if (PasswordHashing.Validate(password, email) is { } problem) { Console.Error.WriteLine(problem); return 2; }
        }

        if (existing == null)
        {
            var name = Text.Clean(Option(args, "name"), 120) ?? email;
            var id = await users.CreateAsync(new UserRow
            {
                Email = email, FullName = name, PasswordHash = hashing.Hash(password!), IsSiteAdmin = true, EmailVerifiedAt = now,
                SecurityStamp = Tokens.NewSecurityStamp(), CreatedAt = now, UpdatedAt = now
            });
            Console.WriteLine($"Created website administrator {name} <{email}> (user {id}).");
        }
        else
        {
            await users.SetSiteAdminAsync(existing.Id, true, now);
            await users.SetEmailVerifiedAsync(existing.Id, now);
            if (existing.IsDisabled) await users.SetDisabledAsync(existing.Id, false, Tokens.NewSecurityStamp(), now);
            if (password != null) await users.SetPasswordAsync(existing.Id, hashing.Hash(password), Tokens.NewSecurityStamp(), now);
            Console.WriteLine($"{existing.FullName} <{email}> is now a website administrator{(password != null ? " (password changed)" : "")}.");
        }
        return 0;
    }

    private static string AskPassword()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? "";
        while (true)
        {
            var first = ReadHidden("Password (at least 8 characters): ");
            var second = ReadHidden("Repeat the password: ");
            if (first == second) return first;
            Console.WriteLine("The two passwords differ, try again.");
        }
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static async Task<int> CheckAsync(IServiceProvider sp)
    {
        var ok = true;
        void Report(bool good, string what)
        {
            ok &= good;
            Console.WriteLine($"[{(good ? " ok " : "FAIL")}] {what}");
        }

        var site = sp.GetRequiredService<IOptions<SiteOptions>>().Value;
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        Console.WriteLine($"Practical Music website {Api.ApiEndpoints.Version} - environment {env.EnvironmentName}");
        Console.WriteLine($"Public address (Site:BaseUrl): {(string.IsNullOrWhiteSpace(site.BaseUrl) ? "(taken from each request)" : site.BaseUrl)}");

        var paths = sp.GetRequiredService<DataPaths>();
        try
        {
            paths.EnsureCreated();
            var probe = paths.NewTempFile();
            await File.WriteAllTextAsync(probe, "probe");
            File.Delete(probe);
            Report(true, $"data folder {paths.Root} is writable");
        }
        catch (Exception ex) { Report(false, $"data folder {paths.Root}: {ex.Message}"); }

        var schema = sp.GetRequiredService<ISchemaInfo>();
        try
        {
            var version = await schema.GetSchemaVersionAsync();
            Report(version >= StartupChecks.RequiredSchemaVersion,
                version == null ? $"{schema.Describe()}: tables missing - run database/02-schema.sql" : $"{schema.Describe()}: schema version {version}");
            var users = sp.GetRequiredService<IUserRepository>();
            var admins = await users.CountSiteAdminsAsync();
            Report(admins > 0, admins > 0 ? $"{admins} website administrator(s)" : "no website administrator yet - run create-admin");
        }
        catch (Exception ex) { Report(false, $"{schema.Describe()}: {ex.Message}"); }

        try
        {
            using var doc = PdfDocument.Load(StartupChecks.TinyPdf);
            var image = doc.Render(0, 100);
            Report(image.Width > 0, $"PDF engine (PDFium) renders pages ({image.Width}x{image.Height})");
        }
        catch (Exception ex) { Report(false, "PDF engine (PDFium): " + ex.Message + " - is libpdfium.so next to the program?"); }

        var email = sp.GetRequiredService<IOptions<EmailOptions>>().Value;
        var transport = sp.GetRequiredService<IMailTransport>();
        Report(!email.IsDisabled, "e-mail: " + transport.Describe());
        if (email.IsSmtp && email.FromAddress.EndsWith("example.com", StringComparison.OrdinalIgnoreCase))
            Report(false, "Email:FromAddress is still the example address");
        Console.WriteLine(ok ? "Everything looks good." : "Fix the problems above (see docs/INSTALL-Server-Debian.md).");
        return ok ? 0 : 1;
    }

    private static async Task<int> SendTestEmailAsync(IServiceProvider sp, string[] args)
    {
        var to = Text.NormalizeEmail(Option(args, "to"));
        if (!Text.IsValidEmail(to)) { Console.Error.WriteLine("Use: send-test-email --to you@example.com"); return 2; }
        var site = sp.GetRequiredService<IOptions<SiteOptions>>().Value;
        var transport = sp.GetRequiredService<IMailTransport>();
        Console.WriteLine("Sending via " + transport.Describe() + " …");
        var content = EmailTemplates.Test(site.Name, to);
        await transport.SendAsync(new EmailRow { Id = 0, ToAddress = to, Subject = content.Subject, BodyText = content.Text, BodyHtml = content.Html }, CancellationToken.None);
        Console.WriteLine("Sent. Check the inbox (and the spam folder) of " + to + ".");
        return 0;
    }

    private static async Task<int> PasswordLinkAsync(IServiceProvider sp, string[] args)
    {
        var email = Text.NormalizeEmail(Option(args, "email"));
        var users = sp.GetRequiredService<IUserRepository>();
        var user = await users.FindByEmailAsync(email);
        if (user == null) { Console.Error.WriteLine("No account with that e-mail address."); return 2; }
        var token = await sp.GetRequiredService<UserTokenService>().CreateAsync(user.Id, TokenPurpose.ResetPassword, TimeSpan.FromHours(24));
        var site = sp.GetRequiredService<IOptions<SiteOptions>>().Value;
        var baseUrl = string.IsNullOrWhiteSpace(site.BaseUrl) ? "https://<your website>" : site.BaseUrl.TrimEnd('/');
        Console.WriteLine($"One-time link for {user.FullName} <{user.Email}>, valid for 24 hours:");
        Console.WriteLine($"{baseUrl}/Account/ResetPassword?token={Uri.EscapeDataString(token)}");
        return 0;
    }
}

public static class StartupChecks
{
    public const int RequiredSchemaVersion = 1;

    /// <summary>A one-page PDF (PDFium tolerates the missing cross-reference table).</summary>
    public static readonly byte[] TinyPdf = System.Text.Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 260]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    /// <summary>
    /// Settings a public website cannot do without. Site:BaseUrl is required outside development: the links in e-mails
    /// and the "server" stamped into downloaded books must not depend on the Host header a visitor sends.
    /// </summary>
    public static void VerifySettings(WebApplication app)
    {
        var site = app.Services.GetRequiredService<IOptions<SiteOptions>>().Value;
        var env = app.Environment;
        if (env.IsDevelopment() || env.IsEnvironment("Testing")) return;
        if (string.IsNullOrWhiteSpace(site.BaseUrl) || !Uri.TryCreate(site.BaseUrl, UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "http"))
            throw new InvalidOperationException(
                "Site:BaseUrl must be set to the website's public address, e.g. \"https://practicalmusic.example.org\" " +
                "(in /etc/practicalmusic/appsettings.json - see docs/INSTALL-Server-Debian.md).");
    }

    /// <summary>Waits for the database (it may still be starting) and refuses to run against a missing or outdated schema.</summary>
    public static async Task VerifyDatabaseAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        using var scope = app.Services.CreateScope();
        var schema = scope.ServiceProvider.GetRequiredService<ISchemaInfo>();
        for (var attempt = 1; ; attempt++)
        {
            int? version;
            try
            {
                version = await schema.GetSchemaVersionAsync();
            }
            catch (Exception ex) when (attempt < 10)
            {
                log.LogWarning("Database not reachable yet ({Error}); retrying ({Attempt}/10)…", ex.Message, attempt);
                await Task.Delay(TimeSpan.FromSeconds(3));
                continue;
            }
            if (version == null)
                throw new InvalidOperationException($"{schema.Describe()}: the tables do not exist. Run database/01-create-database-and-user.sql and database/02-schema.sql first.");
            if (version < RequiredSchemaVersion)
                throw new InvalidOperationException($"{schema.Describe()}: schema version {version} is older than {RequiredSchemaVersion}. Apply the newer scripts in the database folder.");
            if (version > RequiredSchemaVersion)
                log.LogWarning("Database schema version {Version} is newer than this website ({Required}); update the website.", version, RequiredSchemaVersion);
            log.LogInformation("Database: {Database}, schema version {Version}", schema.Describe(), version);
            return;
        }
    }
}
