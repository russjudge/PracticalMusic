using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages;

/// <summary>Common helpers of every page: services, the signed-in user, one-off messages after a redirect.</summary>
public abstract class AppPageModel : PageModel
{
    [TempData] public string? FlashMessage { get; set; }
    [TempData] public string? FlashError { get; set; }

    protected T Svc<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    protected CurrentUser Me => Svc<CurrentUser>();
    protected SiteOptions Site => Svc<IOptions<SiteOptions>>().Value;

    /// <summary>The signed-in user (pages in authorized folders always have one).</summary>
    protected async Task<UserRow> UserAsync() =>
        await Me.GetAsync() ?? throw new InvalidOperationException("No signed-in user.");

    protected IActionResult RedirectWith(string message, string page, object? routeValues = null)
    {
        FlashMessage = message;
        return RedirectToPage(page, routeValues);
    }

    protected IActionResult RedirectWithError(string message, string page, object? routeValues = null)
    {
        FlashError = message;
        return RedirectToPage(page, routeValues);
    }

    /// <summary>Runs an action that may throw <see cref="UserFacingException"/>; the message is shown on the page instead.</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return false;
        }
    }
}

/// <summary>Pages of one organization (/Org/{orgId}/…). Members may look; <see cref="RequireManager"/> pages are for managers.</summary>
public abstract class OrgPageModel : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long OrgId { get; set; }

    public OrgRow Org { get; private set; } = new();
    public string Role { get; private set; } = OrgRoles.Member;
    public bool IsManager => Role == OrgRoles.Manager;
    public bool IsSiteAdmin { get; private set; }

    protected virtual bool RequireManager => false;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var org = await Svc<IOrganizationRepository>().GetAsync(OrgId);
        var role = org == null ? null : await Me.RoleInAsync(org.Id);
        if (org == null || role == null)
        {
            context.Result = NotFound();
            return;
        }
        IsSiteAdmin = await Me.IsSiteAdminAsync();
        if (!org.IsActive && !IsSiteAdmin)
        {
            context.Result = NotFound();
            return;
        }
        if (RequireManager && role != OrgRoles.Manager)
        {
            context.Result = Forbid();
            return;
        }
        Org = org;
        Role = role;
        await next();
    }
}

/// <summary>Pages only the website administrator may open.</summary>
public abstract class AdminPageModel : AppPageModel
{
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (!await Me.IsSiteAdminAsync())
        {
            context.Result = Forbid();
            return;
        }
        await next();
    }
}

public static class Display
{
    public static string When(DateTime? utc) => utc == null ? "–" : utc.Value.ToString("yyyy-MM-dd HH:mm") + " UTC";
    public static string Date(DateTime? utc) => utc == null ? "–" : utc.Value.ToString("yyyy-MM-dd");
    public static string RoleName(string role) => role == OrgRoles.Manager ? "Manager" : "Member";

    public static string StatusName(string status) => status switch
    {
        MembershipStatus.Active => "Has access",
        MembershipStatus.Pending => "Waiting for approval",
        MembershipStatus.Invited => "Invited (not accepted yet)",
        MembershipStatus.Rejected => "Declined",
        _ => status
    };
}
