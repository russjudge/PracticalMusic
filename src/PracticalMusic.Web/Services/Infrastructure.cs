using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PracticalMusic.Core.Models;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Services;

/// <summary>Folders under Site:DataDir.</summary>
public sealed class DataPaths
{
    public DataPaths(IOptions<SiteOptions> site, IWebHostEnvironment env)
    {
        var dir = site.Value.DataDir;
        Root = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir));
    }

    /// <summary>For tests.</summary>
    public DataPaths(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    /// <summary>Uploaded files, named by their SHA-256.</summary>
    public string Media => Path.Combine(Root, "media");
    /// <summary>Rendered page images (safe to delete).</summary>
    public string PageCache => Path.Combine(Root, "cache", "pages");
    /// <summary>Built .pmbook downloads (safe to delete).</summary>
    public string Packages => Path.Combine(Root, "cache", "packages");
    public string Temp => Path.Combine(Root, "tmp");
    /// <summary>Encryption keys of the sign-in cookies. Keep it: deleting it signs everyone out.</summary>
    public string Keys => Path.Combine(Root, "keys");
    /// <summary>Installers offered on the Downloads page (copy them here).</summary>
    public string Downloads => Path.Combine(Root, "downloads");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Media, PageCache, Packages, Temp, Keys, Downloads }) Directory.CreateDirectory(d);
    }

    public string MediaFile(string sha256) => Path.Combine(Media, sha256[..2], sha256);

    public string NewTempFile(string extension = ".tmp")
    {
        Directory.CreateDirectory(Temp);
        return Path.Combine(Temp, Guid.NewGuid().ToString("N") + extension);
    }
}

/// <summary>Absolute links for e-mails and for the "server" of downloaded books.</summary>
public sealed class SiteUrls(IOptions<SiteOptions> site, IHttpContextAccessor http)
{
    public string BaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(site.Value.BaseUrl)) return OnlineOrigin.NormalizeServer(site.Value.BaseUrl);
            var request = http.HttpContext?.Request;
            return request == null ? "http://localhost" : OnlineOrigin.NormalizeServer($"{request.Scheme}://{request.Host}{request.PathBase}");
        }
    }

    public string Absolute(string pathAndQuery) => BaseUrl + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
}

/// <summary>Records who did what (shown to the administrator and to organization managers).</summary>
public sealed class Audit(IAuditRepository repo, IHttpContextAccessor http, ILogger<Audit> log)
{
    public async Task WriteAsync(string action, string? details, long? userId, long? orgId = null)
    {
        try
        {
            await repo.WriteAsync(new AuditRow
            {
                At = DateTime.UtcNow,
                UserId = userId,
                OrgId = orgId,
                Action = action,
                Details = details is { Length: > 2000 } ? details[..2000] : details,
                Ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString()
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not write audit entry {Action}", action); // never fail the user's action because of the log
        }
    }
}

/// <summary>Simple per-key hourly limits for actions that send e-mail (kept in memory; a restart resets them).</summary>
public sealed class Throttle(IMemoryCache cache)
{
    /// <summary>True (and counted) while fewer than <paramref name="perHour"/> calls were made for the key in the last hour.</summary>
    public bool Allow(string key, int perHour)
    {
        var count = cache.GetOrCreate("throttle:" + key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new int[1];
        })!;
        lock (count)
        {
            if (count[0] >= perHour) return false;
            count[0]++;
            return true;
        }
    }
}

public static class ClientAddress
{
    /// <summary>
    /// The key rate limits count by: the IPv4 address, or the /64 network of an IPv6 address (one home or phone
    /// usually has a whole /64 and could otherwise use a new address for every attempt).
    /// </summary>
    public static string Key(System.Net.IPAddress? ip)
    {
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return "v6:" + Convert.ToHexString(ip.GetAddressBytes(), 0, 8);
        return ip.ToString();
    }
}

/// <summary>
/// How large request bodies may be. Every request starts with a small limit (forms, sign-in); the JSON API allows
/// whole books; only the two upload endpoints raise it to Site:MaxUploadMegabytes - and only after checking that
/// the user manages the organization, so nobody else can make the server receive hundreds of megabytes.
/// </summary>
public static class RequestLimits
{
    public const long Form = 256 * 1024;
    /// <summary>Book saves (book.json is limited to 12 MB, see BookService).</summary>
    public const long Json = 24L * 1024 * 1024;

    public static long DefaultFor(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") && !request.Path.StartsWithSegments("/api/auth") ? Json : Form;

    public static void Apply(HttpContext context, long bytes)
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = bytes;
    }

    /// <summary>Call before reading the body of an upload the user is allowed to make.</summary>
    public static void AllowUpload(HttpContext context, SiteOptions site) =>
        Apply(context, (site.MaxUploadMegabytes + 2) * 1024L * 1024L);
}

/// <summary>A problem the user can fix; its message is shown to them as is.</summary>
public sealed class UserFacingException(string message, int statusCode = StatusCodes.Status400BadRequest, string code = "invalid", int? currentRevision = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? CurrentRevision { get; } = currentRevision;

    public static UserFacingException Forbidden(string message = "You do not have permission for this.") => new(message, StatusCodes.Status403Forbidden, "forbidden");
    public static UserFacingException NotFound(string message = "Not found.") => new(message, StatusCodes.Status404NotFound, "not_found");
}

public static class Text
{
    /// <summary>Trims and caps free text from forms; empty becomes null.</summary>
    public static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        return s.Length > max ? s[..max] : s;
    }

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>A plausible address (the real check is the confirmation e-mail).</summary>
    public static bool IsValidEmail(string? email)
    {
        var e = NormalizeEmail(email);
        if (e.Length is < 3 or > 254 || e.Contains(' ') || e.Count(c => c == '@') != 1) return false;
        var at = e.IndexOf('@');
        var domain = e[(at + 1)..];
        return at > 0 && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    public static string Bytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";
}
