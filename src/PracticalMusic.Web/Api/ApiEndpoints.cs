using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Api;

/// <summary>Turns <see cref="UserFacingException"/> into the JSON error every client understands.</summary>
public sealed class ApiErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UserFacingException ex)
        {
            return Results.Json(new ApiError { Error = ex.Message, Code = ex.Code, CurrentRevision = ex.CurrentRevision }, OnlineJson.Options, statusCode: ex.StatusCode);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(new ApiError { Error = "The upload is larger than this website accepts.", Code = "too_large" }, OnlineJson.Options, statusCode: 413);
        }
        catch (Exception ex) when (DbErrors.IsDuplicateKey(ex))
        {
            return Results.Json(new ApiError { Error = "Someone else made the same change at the same moment. Reload and try again.", Code = "conflict" },
                OnlineJson.Options, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

/// <summary>
/// Browser requests that change something must carry the anti-forgery token (header X-CSRF-TOKEN), so another
/// website cannot make a signed-in browser act. The apps authenticate with a bearer token and are exempt.
/// </summary>
public sealed class CsrfFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var safe = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method);
        var bearer = http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (!safe && !bearer)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Json(new ApiError { Error = "Your page is out of date. Reload it and try again.", Code = "csrf" }, OnlineJson.Options, statusCode: 400);
            }
        }
        return await next(context);
    }
}

public static class ApiEndpoints
{
    public static readonly string Version = typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                                            ?? typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static void MapPracticalMusicApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<ApiErrorFilter>();

        // ---------------------------------------------------------------- anonymous
        api.MapGet("/info", (IOptions<SiteOptions> site) => Results.Json(new
        {
            product = "Practical Music",
            name = site.Value.Name,
            version = Version,
            syncProtocol = SyncWireMessage.ProtocolVersion
        }, OnlineJson.Options));

        api.MapPost("/auth/login", async (LoginRequest request, AccountService accounts) =>
        {
            var result = await accounts.CheckPasswordAsync(request.Email, request.Password);
            return result.Status switch
            {
                LoginStatus.Success => Results.Json(await accounts.IssueApiTokenAsync(result.User!, request.DeviceName, request.Platform), OnlineJson.Options),

                LoginStatus.NotVerified => Error(403, "Confirm your e-mail address first: open the link in the e-mail we sent you (the website's sign-in page can send it again).", "not_verified"),
                LoginStatus.Disabled => Error(403, result.Message ?? "This account is disabled.", "disabled"),
                _ => Error(401, AccountService.LoginFailedMessage, "invalid_login") // wrong password, unknown address and lockout look alike
            };
        }).RequireRateLimiting("auth");

        // ---------------------------------------------------------------- signed in (cookie or app token)
        var auth = api.MapGroup("").RequireAuthorization().AddEndpointFilter<CsrfFilter>();

        auth.MapPost("/auth/logout", async (CurrentUser current, ITokenRepository tokens, UserCache cache) =>
        {
            if (current.ApiTokenId is { } tokenId && current.Id is { } userId)
            {
                var row = (await tokens.ListApiTokensAsync(userId)).FirstOrDefault(t => t.Id == tokenId);
                await tokens.DeleteApiTokenAsync(tokenId, userId);
                if (row != null) cache.InvalidateToken(row.TokenHash);
            }
            return Results.NoContent();
        });

        auth.MapGet("/me", async (CurrentUser current, AccountService accounts) =>
        {
            var user = await current.GetAsync() ?? throw new UserFacingException("Not signed in.", 401, "unauthorized");
            return Results.Json(await accounts.BuildUserInfoAsync(user), OnlineJson.Options);
        });

        auth.MapGet("/orgs", async (CurrentUser current) => Results.Json(await current.AccessibleOrganizationsAsync(), OnlineJson.Options));

        auth.MapGet("/orgs/{orgId:long}/books", async (long orgId, CurrentUser current, IBookRepository books) =>
        {
            await RequireViewAsync(current, orgId);
            return Results.Json((await books.ListForOrgAsync(orgId)).Select(BookService.ToSummary).ToList(), OnlineJson.Options);
        });

        auth.MapPost("/orgs/{orgId:long}/books", async (long orgId, SaveBookRequest request, CurrentUser current, BookService service) =>
        {
            var user = await RequireManageAsync(current, orgId);
            var result = await service.SaveAsync(orgId, null, request.Book, request.Media, null, request.Force, user.Id);
            return Results.Json(result, OnlineJson.Options, statusCode: StatusCodes.Status201Created);
        });

        auth.MapPost("/orgs/{orgId:long}/books/package", async (long orgId, int? baseRevision, bool? force, bool? asNew,
            HttpRequest request, CurrentUser current, PackageService packages, IOptions<SiteOptions> site, CancellationToken ct) =>
        {
            var user = await RequireManageAsync(current, orgId);
            RequestLimits.AllowUpload(request.HttpContext, site.Value);
            Stream body;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault() ?? throw new UserFacingException("Choose a .pmbook file.");
                body = file.OpenReadStream();
            }
            else
            {
                body = request.Body;
            }
            await using (body)
            {
                var result = await packages.ImportAsync(orgId, body, baseRevision, force == true, asNew == true, user.Id, ct);
                return Results.Json(result, OnlineJson.Options, statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            }
        });

        auth.MapGet("/books/{id:long}", async (long id, CurrentUser current, IBookRepository books, IOrganizationRepository orgs, BookService service) =>
        {
            var row = await books.GetAsync(id) ?? throw UserFacingException.NotFound("Book not found.");
            await RequireViewAsync(current, row.OrgId);
            var org = await orgs.GetAsync(row.OrgId);
            return Results.Json(await service.GetDocumentAsync(row, org?.Name ?? "", await current.CanManageAsync(row.OrgId)), OnlineJson.Options);
        });

        auth.MapGet("/books/{id:long}/summary", async (long id, CurrentUser current, IBookRepository books) =>
        {
            var row = await books.GetAsync(id) ?? throw UserFacingException.NotFound("Book not found.");
            await RequireViewAsync(current, row.OrgId);
            return Results.Json(BookService.ToSummary(row), OnlineJson.Options);
        });

        auth.MapPut("/books/{id:long}", async (long id, SaveBookRequest request, CurrentUser current, IBookRepository books, BookService service) =>
        {
            var row = await books.GetAsync(id) ?? throw UserFacingException.NotFound("Book not found.");
            var user = await RequireManageAsync(current, row.OrgId);
            return Results.Json(await service.SaveAsync(row.OrgId, id, request.Book, request.Media, request.BaseRevision, request.Force, user.Id), OnlineJson.Options);
        });

        auth.MapDelete("/books/{id:long}", async (long id, CurrentUser current, IBookRepository books, Audit audit) =>
        {
            var row = await books.GetAsync(id) ?? throw UserFacingException.NotFound("Book not found.");
            var user = await RequireManageAsync(current, row.OrgId);
            await books.DeleteAsync(id);
            await audit.WriteAsync("book.deleted", $"\"{row.Title}\"", user.Id, row.OrgId);
            return Results.NoContent();
        });

        auth.MapGet("/books/{id:long}/package", async (long id, HttpContext http, CurrentUser current, IBookRepository books,
            IOrganizationRepository orgs, PackageService packages, CancellationToken ct) =>
        {
            var row = await books.GetAsync(id) ?? throw UserFacingException.NotFound("Book not found.");
            await RequireViewAsync(current, row.OrgId);
            var org = await orgs.GetAsync(row.OrgId) ?? throw UserFacingException.NotFound();
            var path = await packages.GetPackageAsync(row, org, ct);
            http.Response.Headers[OnlineHeaders.Revision] = row.Revision.ToString();
            http.Response.Headers[OnlineHeaders.Fingerprint] = row.Fingerprint;
            http.Response.Headers.CacheControl = "private, no-cache";
            return Results.File(path, "application/octet-stream", PackageService.DownloadName(row.Title), enableRangeProcessing: true);
        });

        auth.MapPost("/orgs/{orgId:long}/media", async (long orgId, HttpRequest request, CurrentUser current, MediaStorage storage,
            IOptions<SiteOptions> site, CancellationToken ct) =>
        {
            var user = await RequireManageAsync(current, orgId);
            RequestLimits.AllowUpload(request.HttpContext, site.Value);
            if (!request.HasFormContentType) throw new UserFacingException("Send the file as multipart/form-data.");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault() ?? throw new UserFacingException("Choose a file.");
            await using var stream = file.OpenReadStream();
            var row = await storage.ImportAsync(orgId, stream, file.FileName, site.Value.MaxUploadMegabytes * 1024L * 1024L, user.Id, ct);
            return Results.Json(BookService.ToInfo(row), OnlineJson.Options);
        });

        auth.MapGet("/media/{id:long}/file", async (long id, bool? download, HttpContext http, CurrentUser current, MediaStorage storage) =>
        {
            var media = await storage.GetAsync(id) ?? throw UserFacingException.NotFound("File not found.");
            await RequireViewAsync(current, media.OrgId);
            var path = storage.FilePath(media);
            if (!File.Exists(path)) throw UserFacingException.NotFound("The file is missing on the server.");
            http.Response.Headers.CacheControl = "private, max-age=604800, immutable"; // content-addressed: never changes
            return Results.File(path, media.ContentType, download == true ? media.FileName : null,
                entityTag: new EntityTagHeaderValue("\"" + media.Sha256 + "\""), enableRangeProcessing: true);
        });

        auth.MapGet("/media/{id:long}/pages/{page:int}", async (long id, int page, int? w, HttpContext http, CurrentUser current,
            MediaStorage storage, PageRenderService renderer, CancellationToken ct) =>
        {
            var media = await storage.GetAsync(id) ?? throw UserFacingException.NotFound("File not found.");
            await RequireViewAsync(current, media.OrgId);
            var path = await renderer.GetPageImageAsync(media, storage.FilePath(media), page, Math.Clamp(w ?? 1200, 100, 4000), ct);
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, "image/png", entityTag: new EntityTagHeaderValue($"\"{media.Sha256[..16]}-{page}-{PageRenderService.BucketWidth(w ?? 1200)}\""));
        });
    }

    private static IResult Error(int status, string message, string code) =>
        Results.Json(new ApiError { Error = message, Code = code }, OnlineJson.Options, statusCode: status);

    private static async Task RequireViewAsync(CurrentUser current, long orgId)
    {
        if (!await current.CanViewAsync(orgId)) throw UserFacingException.Forbidden("You are not a member of this organization.");
    }

    private static async Task<UserRow> RequireManageAsync(CurrentUser current, long orgId)
    {
        var user = await current.GetAsync() ?? throw new UserFacingException("Not signed in.", 401, "unauthorized");
        if (!await current.CanManageAsync(orgId))
            throw UserFacingException.Forbidden("Only the organization's managers can change its books.");
        return user;
    }
}
