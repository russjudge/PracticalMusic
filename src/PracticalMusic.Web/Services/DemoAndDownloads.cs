using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;

namespace PracticalMusic.Web.Services;

/// <summary>
/// Sample accounts for trying the website on a development machine with Database:Provider = Memory and Demo:Seed = true
/// (see appsettings.Development.json). Never used with MariaDB.
/// </summary>
public static class DemoData
{
    public static async Task SeedAsync(WebApplication app)
    {
        var config = app.Configuration;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");
        log.LogWarning("Database:Provider = Memory - everything is kept in memory and lost when the website stops. For development only.");
        if (!config.GetValue<bool>("Demo:Seed")) return;

        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<IUserRepository>();
        var orgs = sp.GetRequiredService<IOrganizationRepository>();
        var hashing = sp.GetRequiredService<PasswordHashing>();
        var password = config["Demo:Password"] ?? "demo-password-123";
        var now = DateTime.UtcNow;

        async Task<long> User(string email, string name, bool admin) => await users.CreateAsync(new UserRow
        {
            Email = email, FullName = name, PasswordHash = hashing.Hash(password), IsSiteAdmin = admin, EmailVerifiedAt = now,
            SecurityStamp = Tokens.NewSecurityStamp(), CreatedAt = now, UpdatedAt = now
        });

        var admin = await User("admin@example.com", "Website Administrator", true);
        var manager = await User("manager@example.com", "Morgan Manager", false);
        var member = await User("member@example.com", "Mel Member", false);
        var org = await orgs.CreateAsync(new OrgRow
        {
            Name = "Demo Church", Kind = "church", Description = "Sample organization of the demo mode.", AllowJoinRequests = true,
            IsActive = true, CreatedAt = now, CreatedBy = admin
        });
        await orgs.CreateAsync(new OrgRow { Name = "Demo Band", Kind = "band", AllowJoinRequests = true, IsActive = true, CreatedAt = now, CreatedBy = admin });
        await orgs.UpsertMembershipAsync(new MembershipRow { UserId = manager, OrgId = org, Role = OrgRoles.Manager, Status = MembershipStatus.Active, RequestedAt = now, DecidedAt = now });
        await orgs.UpsertMembershipAsync(new MembershipRow { UserId = member, OrgId = org, Role = OrgRoles.Member, Status = MembershipStatus.Active, RequestedAt = now, DecidedAt = now });

        var bookPath = config["Demo:BookPath"];
        if (!string.IsNullOrWhiteSpace(bookPath))
        {
            var full = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, bookPath));
            if (File.Exists(full))
            {
                try
                {
                    await using var stream = File.OpenRead(full);
                    var result = await sp.GetRequiredService<PackageService>().ImportAsync(org, stream, null, true, false, manager, CancellationToken.None);
                    log.LogInformation("Demo book \"{Title}\" imported", result.Title);
                }
                catch (Exception ex) { log.LogWarning("Demo book {Path} not imported: {Error}", full, ex.Message); }
            }
        }
        log.LogWarning("Demo accounts (password \"{Password}\"): admin@example.com, manager@example.com, member@example.com", password);
    }
}

/// <summary>The installers an administrator placed in &lt;DataDir&gt;/downloads.</summary>
public static class DownloadsInfo
{
    public sealed record Item(string FileName, string Platform, long Size, DateTime Modified);

    public static List<Item> List(DataPaths paths)
    {
        if (!Directory.Exists(paths.Downloads)) return new List<Item>();
        return new DirectoryInfo(paths.Downloads).EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.'))
            .Select(f => new Item(f.Name, Platform(f.Name), f.Length, f.LastWriteTimeUtc))
            .OrderBy(i => i.Platform).ThenByDescending(i => i.Modified)
            .ToList();
    }

    public static string Platform(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".apk")) return "Android";
        if (n.Contains("win") || n.EndsWith(".exe") || n.EndsWith(".msi")) return "Windows";
        if (n.Contains("linux") || n.EndsWith(".tar.gz") || n.EndsWith(".pkg.tar.zst") || n.EndsWith(".deb")) return "Linux";
        return "Other";
    }

    public static string ContentType(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".apk")) return "application/vnd.android.package-archive";
        if (n.EndsWith(".zip")) return "application/zip";
        if (n.EndsWith(".gz")) return "application/gzip";
        if (n.EndsWith(".zst")) return "application/zstd";
        if (n.EndsWith(".exe")) return "application/vnd.microsoft.portable-executable";
        if (n.EndsWith(".pdf")) return "application/pdf";
        if (n.EndsWith(".txt") || n.EndsWith(".md")) return "text/plain; charset=utf-8";
        return "application/octet-stream";
    }
}
