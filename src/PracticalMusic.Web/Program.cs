using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using PracticalMusic.Core.Online;
using PracticalMusic.Web;
using PracticalMusic.Web.Api;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Data.MariaDb;
using PracticalMusic.Web.Data.Memory;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------ configuration
// appsettings.json (defaults, shipped) → /etc/practicalmusic/appsettings.json or $PRACTICALMUSIC_CONFIG (this host's
// settings and secrets, outside the program folder so upgrades never touch it) → PRACTICALMUSIC_* environment variables.
var configFile = Environment.GetEnvironmentVariable("PRACTICALMUSIC_CONFIG");
if (string.IsNullOrWhiteSpace(configFile) && OperatingSystem.IsLinux() && File.Exists("/etc/practicalmusic/appsettings.json"))
    configFile = "/etc/practicalmusic/appsettings.json";
if (!string.IsNullOrWhiteSpace(configFile)) builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("PRACTICALMUSIC_");

builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
var site = builder.Configuration.GetSection(SiteOptions.Section).Get<SiteOptions>() ?? new SiteOptions();
var database = builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
var emailOptions = builder.Configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
var maxUpload = (site.MaxUploadMegabytes + 16) * 1024L * 1024L;

builder.WebHost.ConfigureKestrel(k =>
{
    // the per-request limits are set below (RequestLimits); uploads raise theirs after the permission check
    k.Limits.MaxRequestBodySize = RequestLimits.Json;
    k.AddServerHeader = false;
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxUpload;
    o.ValueLengthLimit = 1024 * 1024;
});

// ------------------------------------------------------------------ storage
if (database.IsMemory)
{
    var store = new InMemoryStore();
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton<IUserRepository>(store);
    builder.Services.AddSingleton<IOrganizationRepository>(store);
    builder.Services.AddSingleton<IMediaRepository>(store);
    builder.Services.AddSingleton<IBookRepository>(store);
    builder.Services.AddSingleton<ITokenRepository>(store);
    builder.Services.AddSingleton<IEmailOutboxRepository>(store);
    builder.Services.AddSingleton<IAuditRepository>(store);
    builder.Services.AddSingleton<ISchemaInfo>(store);
}
else
{
    builder.Services.AddSingleton<Db>();
    builder.Services.AddSingleton<IUserRepository, MariaDbUserRepository>();
    builder.Services.AddSingleton<IOrganizationRepository, MariaDbOrganizationRepository>();
    builder.Services.AddSingleton<IMediaRepository, MariaDbMediaRepository>();
    builder.Services.AddSingleton<IBookRepository, MariaDbBookRepository>();
    builder.Services.AddSingleton<ITokenRepository, MariaDbTokenRepository>();
    builder.Services.AddSingleton<IEmailOutboxRepository, MariaDbEmailOutboxRepository>();
    builder.Services.AddSingleton<IAuditRepository, MariaDbAuditRepository>();
    builder.Services.AddSingleton<ISchemaInfo, MariaDbSchemaInfo>();
}

// ------------------------------------------------------------------ services
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<PasswordHashing>();
builder.Services.AddSingleton<UserCache>();
builder.Services.AddSingleton<SiteUrls>();
builder.Services.AddSingleton<EmailSignal>();
builder.Services.AddSingleton<PageRenderService>();
builder.Services.AddSingleton<SyncHub>();
builder.Services.AddSingleton<Throttle>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Audit>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<UserTokenService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<MediaStorage>();
builder.Services.AddScoped<BookService>();
builder.Services.AddScoped<PackageService>();
if (emailOptions.IsSmtp) builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();
else if (emailOptions.IsPickup) builder.Services.AddSingleton<IMailTransport, PickupMailTransport>();
else builder.Services.AddSingleton<IMailTransport, DisabledMailTransport>();

var cliMode = Cli.IsCommand(args);
if (!cliMode)
{
    builder.Services.AddHostedService<EmailSenderService>();
    builder.Services.AddHostedService<MaintenanceService>();
}

// ------------------------------------------------------------------ security
builder.Services.AddPracticalMusicAuth();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "PracticalMusic.Csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
var dataRoot = Path.GetFullPath(Path.IsPathRooted(site.DataDir) ? site.DataDir : Path.Combine(builder.Environment.ContentRootPath, site.DataDir));
Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));
builder.Services.AddDataProtection()
    .SetApplicationName("PracticalMusic")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // sign-in, sign-up and password e-mails: Site:AuthRequestsPerMinute (20) submissions per minute from one address
    // (an IPv6 /64 counts as one address); just opening those pages is not counted
    o.AddPolicy("auth", ctx =>
        HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
            ? RateLimitPartition.GetNoLimiter("page-views")
            : RateLimitPartition.GetFixedWindowLimiter(ClientAddress.Key(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, site.AuthRequestsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        if (AuthSetup.IsApiRequest(ctx.HttpContext.Request))
            await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiError { Error = "Too many attempts. Wait a minute and try again.", Code = "rate_limited" }, OnlineJson.Options, ct);
        else
            await ctx.HttpContext.Response.WriteAsync("Too many attempts. Wait a minute and try again.", ct);
    };
});

// Apache (or another reverse proxy) on the same machine terminates HTTPS and forwards the client's address and scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Default: only proxies on this machine (127.0.0.1 / ::1) are trusted.
    foreach (var proxy in builder.Configuration.GetSection("Site:TrustedProxies").Get<string[]>() ?? Array.Empty<string>())
        if (System.Net.IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Account");
    foreach (var page in new[] { "/Account/Login", "/Account/Logout", "/Account/Register", "/Account/VerifyEmail", "/Account/ResendVerification", "/Account/ForgotPassword", "/Account/ResetPassword" })
        o.Conventions.AllowAnonymousToPage(page);
    o.Conventions.AuthorizeFolder("/Org");
    o.Conventions.AuthorizeFolder("/Admin");
    o.Conventions.AuthorizePage("/App");
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

if (cliMode) return await Cli.RunAsync(app, args);

StartupChecks.VerifySettings(app);
app.Services.GetRequiredService<DataPaths>().EnsureCreated();
await StartupChecks.VerifyDatabaseAsync(app);
if (database.IsMemory) await DemoData.SeedAsync(app);

// ------------------------------------------------------------------ pipeline
app.UseForwardedHeaders();
app.Use((ctx, next) =>
{
    RequestLimits.Apply(ctx, RequestLimits.DefaultFor(ctx.Request));
    return next(ctx);
});
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/Status", "?code={0}");

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "same-origin";
    h["X-Frame-Options"] = "DENY";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    var socket = (ctx.Request.IsHttps ? "wss://" : "ws://") + ctx.Request.Host;
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        $"media-src 'self' https: http: blob:; connect-src 'self' {socket}; font-src 'self'; object-src 'none'; " +
        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'; manifest-src 'self'";
    await next();
});

var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
contentTypes.Mappings[".mjs"] = "text/javascript";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        // modules of the music app are fetched without version stamps: always revalidate (cheap with ETags)
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "no-cache";
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapRazorPages();
app.MapPracticalMusicApi();

// synchronized playback (browsers and apps); see SyncHub
app.Map("/ws/sync", async (HttpContext ctx, SyncHub hub, CurrentUser current, UserCache cache, ITokenRepository tokens) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest("WebSocket expected.");
    // a page on another site must not be able to open this socket with the user's cookie
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) &&
        (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !string.Equals(o.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var user = await current.GetAsync();
    if (user == null) return Results.Unauthorized();
    // the connection can stay open for hours: access is checked again regularly (password changed, app signed out,
    // account disabled, membership removed, organization deactivated)
    var userId = user.Id;
    string? stamp = null, tokenHash = null;
    if (current.ViaApiToken)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tokenHash = Tokens.Hash(header[7..].Trim());
    }
    else
    {
        stamp = ctx.User.FindFirst(AuthSchemes.StampClaim)?.Value;
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(socket, userId, user.FullName, orgId => cache.CanStillViewAsync(userId, stamp, tokenHash, tokens, orgId), ctx.RequestAborted);
    return Results.Empty;
}).RequireAuthorization();

app.MapGet("/downloads/{file}", (string file, HttpContext ctx, DataPaths paths, IOptions<SiteOptions> options) =>
{
    if (!options.Value.PublicDownloads && ctx.User.Identity?.IsAuthenticated != true) return Results.Challenge();
    var name = Path.GetFileName(file);
    if (name != file || name.StartsWith('.')) return Results.NotFound();
    var path = Path.Combine(paths.Downloads, name);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(path, DownloadsInfo.ContentType(name), name, enableRangeProcessing: true);
});

app.MapGet("/healthz", () => Results.Text("ok"));

await app.RunAsync();
return 0;

public partial class Program;
