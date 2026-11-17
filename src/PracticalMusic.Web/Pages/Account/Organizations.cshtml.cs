using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

public sealed class OrganizationsModel : AppPageModel
{
    public List<UserOrgRow> Memberships { get; private set; } = new();
    public List<OrgRow> Joinable { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var user = await UserAsync();
        var orgs = Svc<IOrganizationRepository>();
        Memberships = await orgs.ListForUserAsync(user.Id);
        var taken = Memberships.Where(m => m.Status != MembershipStatus.Rejected).Select(m => m.OrgId).ToHashSet();
        Joinable = (await orgs.ListJoinableAsync()).Where(o => !taken.Contains(o.Id)).ToList();
    }

    public async Task<IActionResult> OnPostJoinAsync(long orgId, string? message)
    {
        var user = await UserAsync();
        try
        {
            var result = await Svc<MembershipService>().RequestJoinAsync(user, orgId, message);
            return RedirectWith(result, "/Account/Organizations");
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAcceptAsync(long orgId)
    {
        var user = await UserAsync();
        try
        {
            return RedirectWith(await Svc<MembershipService>().AcceptInvitationAsync(user, orgId), "/Account/Organizations");
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostLeaveAsync(long orgId)
    {
        var user = await UserAsync();
        try
        {
            await Svc<MembershipService>().RemoveAsync(orgId, user.Id, user);
            return RedirectWith("Done.", "/Account/Organizations");
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync();
            return Page();
        }
    }
}
