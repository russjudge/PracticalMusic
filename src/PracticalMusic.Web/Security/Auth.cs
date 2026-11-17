using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Security;

public static class AuthSchemes
{
    /// <summary>Browsers: an encrypted, HttpOnly session cookie.</summary>
    public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;
    /// <summary>Desktop/Android apps: "Authorization: Bearer &lt;token&gt;".</summary>
    public const string Bearer = "Bearer";
    /// <summary>Default: picks Bearer when the header is present, otherwise the cookie.</summary>
    public const string Smart = "Smart";

    public const string UserIdClaim = ClaimTypes.NameIdentifier;
    public const string StampClaim = "pm:stamp";
    public const string TokenClaim = "pm:token";
}

/// <summary>Short-lived cache of user rows and memberships, so the many page-image requests of a book do not each hit the database.</summary>
public sealed class UserCache(IMemoryCache cache, IUserRepository users, IOrganizationRepository orgs)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public async Task<UserRow?> GetUserAsync(long id) =>
        await cache.GetOrCreateAsync("user:" + id, e => { e.AbsoluteExpirationRelativeToNow = Ttl; return users.GetAsync(id); });

    public async Task<List<UserOrgRow>> GetMembershipsAsync(long userId) =>
        await cache.GetOrCreateAsync("memberships:" + userId, e => { e.AbsoluteExpirationRelativeToNow = Ttl; return orgs.ListForUserAsync(userId); })
        ?? new List<UserOrgRow>();

    public void Invalidate(long userId)
    {
        cache.Remove("user:" + userId);
        cache.Remove("memberships:" + userId);
    }

    public void InvalidateToken(string tokenHash) => cache.Remove("token:" + tokenHash);

    /// <summary>Signs out every app of a user right away (the cached tokens included).</summary>
    public async Task RevokeAllAppTokensAsync(ITokenRepository tokens, long userId)
    {
        foreach (var t in await tokens.ListApiTokensAsync(userId)) InvalidateToken(t.TokenHash);
        await tokens.DeleteAllApiTokensAsync(userId);
    }

    /// <summary>
    /// "May this sign-in still be in this organization?" for long-lived connections (synchronized playback): the
    /// account must exist and be enabled, the browser cookie's password stamp or the app's token must still be valid,
    /// and the membership must be active in an active organization (any active organization for the administrator).
    /// Answers are at most 15-30 seconds old.
    /// </summary>
    public async Task<bool> CanStillViewAsync(long userId, string? cookieStamp, string? tokenHash, ITokenRepository tokens, long orgId)
    {
        var user = await GetUserAsync(userId);
        if (user == null || user.IsDisabled) return false;
        if (cookieStamp != null && !string.Equals(user.SecurityStamp, cookieStamp, StringComparison.Ordinal)) return false;
        if (tokenHash != null)
        {
            var token = await GetTokenAsync(tokenHash, tokens);
            if (token == null || token.UserId != userId || token.ExpiresAt < DateTime.UtcNow) return false;
        }
        if (user.IsSiteAdmin)
        {
            var org = await cache.GetOrCreateAsync("org:" + orgId, e => { e.AbsoluteExpirationRelativeToNow = Ttl; return orgs.GetAsync(orgId); });
            return org is { IsActive: true };
        }
        return (await GetMembershipsAsync(userId)).Any(m => m.OrgId == orgId && m.Status == MembershipStatus.Active && m.IsActive);
    }

    /// <summary>Known tokens are cached briefly; unknown ones are not, so requests with random tokens cannot fill the cache.</summary>
    public async Task<ApiTokenRow?> GetTokenAsync(string tokenHash, ITokenRepository tokens)
    {
        if (cache.TryGetValue("token:" + tokenHash, out ApiTokenRow? hit)) return hit;
        var row = await tokens.FindApiTokenAsync(tokenHash);
        if (row != null) cache.Set("token:" + tokenHash, row, TimeSpan.FromSeconds(30));
        return row;
    }
}

/// <summary>The signed-in user of the current request and what they may do.</summary>
public sealed class CurrentUser(IHttpContextAccessor http, UserCache cache, IOrganizationRepository orgs)
{
    private UserRow? _user;
    private bool _loaded;

    private HttpContext? Context => http.HttpContext;

    public long? Id =>
        long.TryParse(Context?.User.FindFirstValue(AuthSchemes.UserIdClaim), out var id) ? id : null;

    public bool IsAuthenticated => Context?.User.Identity?.IsAuthenticated == true && Id != null;

    /// <summary>True when the request was authenticated with an app token rather than the browser cookie.</summary>
    public bool ViaApiToken => Context?.User.HasClaim(c => c.Type == AuthSchemes.TokenClaim) == true;

    public long? ApiTokenId => long.TryParse(Context?.User.FindFirstValue(AuthSchemes.TokenClaim), out var id) ? id : null;

    public async Task<UserRow?> GetAsync()
    {
        if (_loaded) return _user;
        _loaded = true;
        var id = Id;
        if (id == null) return null;
        _user = Context?.Items["pm:user"] as UserRow ?? await cache.GetUserAsync(id.Value);
        if (_user is { IsDisabled: true }) _user = null;
        return _user;
    }

    public async Task<bool> IsSiteAdminAsync() => (await GetAsync())?.IsSiteAdmin == true;

    /// <summary>Active memberships in active organizations.</summary>
    public async Task<List<UserOrgRow>> GetActiveMembershipsAsync()
    {
        var id = Id;
        if (id == null) return new List<UserOrgRow>();
        return (await cache.GetMembershipsAsync(id.Value)).Where(m => m.Status == MembershipStatus.Active && m.IsActive).ToList();
    }

    /// <summary>"manager", "member", or null when the user has no access. The website administrator is a manager everywhere.</summary>
    public async Task<string?> RoleInAsync(long orgId)
    {
        var user = await GetAsync();
        if (user == null) return null;
        if (user.IsSiteAdmin) return OrgRoles.Manager;
        var m = (await GetActiveMembershipsAsync()).FirstOrDefault(x => x.OrgId == orgId);
        return m?.Role;
    }

    public async Task<bool> CanViewAsync(long orgId) => await RoleInAsync(orgId) != null;

    public async Task<bool> CanManageAsync(long orgId) => await RoleInAsync(orgId) == OrgRoles.Manager;

    /// <summary>Organizations the user can open (all active ones for the administrator).</summary>
    public async Task<List<OrgMembershipInfo>> AccessibleOrganizationsAsync()
    {
        var user = await GetAsync();
        if (user == null) return new List<OrgMembershipInfo>();
        if (user.IsSiteAdmin)
        {
            var mine = (await GetActiveMembershipsAsync()).ToDictionary(m => m.OrgId);
            return (await orgs.ListAsync(includeInactive: false))
                .Select(o => new OrgMembershipInfo { Id = o.Id, Name = o.Name, Kind = o.Kind, Role = OrgRoles.Manager, CanEdit = true })
                .OrderByDescending(o => mine.ContainsKey(o.Id)).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return (await GetActiveMembershipsAsync())
            .Select(m => new OrgMembershipInfo { Id = m.OrgId, Name = m.Name, Kind = m.Kind, Role = m.Role, CanEdit = m.Role == OrgRoles.Manager })
            .ToList();
    }

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Authenticates the apps' bearer tokens (see /api/auth/login).</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ITokenRepository tokens, UserCache cache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (token.Length is < 20 or > 200) return AuthenticateResult.Fail("Malformed token.");

        var hash = Tokens.Hash(token);
        var row = await cache.GetTokenAsync(hash, tokens);
        var now = DateTime.UtcNow;
        if (row == null || row.ExpiresAt < now) return AuthenticateResult.Fail("Unknown or expired token.");
        var user = await cache.GetUserAsync(row.UserId);
        if (user == null || user.IsDisabled) return AuthenticateResult.Fail("Account unavailable.");
        Context.Items["pm:user"] = user;

        if (row.LastUsedAt == null || now - row.LastUsedAt.Value > TimeSpan.FromMinutes(10))
        {
            row.LastUsedAt = now; // the cached row too
            await tokens.TouchApiTokenAsync(row.Id, now);
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(AuthSchemes.UserIdClaim, user.Id.ToString()),
            new Claim(AuthSchemes.TokenClaim, row.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email)
        }, AuthSchemes.Bearer);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.Bearer));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new ApiError { Error = "Not signed in, or the sign-in expired. Sign in again.", Code = "unauthorized" }, OnlineJson.Options);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new ApiError { Error = "You do not have permission for this.", Code = "forbidden" }, OnlineJson.Options);
    }
}

public static class AuthSetup
{
    public static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/ws");

    public static void AddPracticalMusicAuth(this IServiceCollection services)
    {
        services.AddAuthentication(AuthSchemes.Smart)
            .AddPolicyScheme(AuthSchemes.Smart, "Cookie or bearer token", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? AuthSchemes.Bearer
                        : AuthSchemes.Cookie;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(AuthSchemes.Bearer, null)
            .AddCookie(AuthSchemes.Cookie, o =>
            {
                o.Cookie.Name = "PracticalMusic.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // https behind Apache (forwarded proto)
                o.LoginPath = "/Account/Login";
                o.LogoutPath = "/Account/Logout";
                o.AccessDeniedPath = "/AccessDenied";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var cache = ctx.HttpContext.RequestServices.GetRequiredService<UserCache>();
                    if (!long.TryParse(ctx.Principal?.FindFirstValue(AuthSchemes.UserIdClaim), out var id))
                    {
                        ctx.RejectPrincipal();
                        return;
                    }
                    var user = await cache.GetUserAsync(id);
                    var stamp = ctx.Principal?.FindFirstValue(AuthSchemes.StampClaim);
                    if (user == null || user.IsDisabled || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
                    {
                        // password changed, account disabled or deleted: this cookie is no longer valid
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookie);
                        return;
                    }
                    ctx.HttpContext.Items["pm:user"] = user;
                };
                o.Events.OnRedirectToLogin = ctx =>
                {
                    if (IsApiRequest(ctx.Request))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return ctx.Response.WriteAsJsonAsync(new ApiError { Error = "Not signed in.", Code = "unauthorized" }, OnlineJson.Options);
                    }
                    ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    if (IsApiRequest(ctx.Request))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return ctx.Response.WriteAsJsonAsync(new ApiError { Error = "You do not have permission for this.", Code = "forbidden" }, OnlineJson.Options);
                    }
                    ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
    }

    /// <summary>Creates the cookie principal for a user who just proved their password.</summary>
    public static ClaimsPrincipal CookiePrincipal(UserRow user) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(AuthSchemes.UserIdClaim, user.Id.ToString()),
            new Claim(AuthSchemes.StampClaim, user.SecurityStamp),
            new Claim(ClaimTypes.Name, user.Email)
        }, AuthSchemes.Cookie));
}
