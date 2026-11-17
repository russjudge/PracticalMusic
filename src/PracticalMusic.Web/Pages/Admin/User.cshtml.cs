using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminUserModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string FullName { get; set; } = "";
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public bool IsSiteAdmin { get; set; }
    [BindProperty] public bool IsDisabled { get; set; }

    public UserRow Target { get; private set; } = new();
    public List<UserOrgRow> Memberships { get; private set; } = new();
    public List<OrgRow> AllOrgs { get; private set; } = new();
    public int AppCount { get; private set; }
    public string? PasswordLink { get; private set; }

    private IUserRepository Users => Svc<IUserRepository>();

    private async Task<bool> LoadAsync()
    {
        var user = await Users.GetAsync(Id);
        if (user == null) return false;
        Target = user;
        Memberships = await Svc<IOrganizationRepository>().ListForUserAsync(Id);
        AllOrgs = await Svc<IOrganizationRepository>().ListAsync(includeInactive: true);
        AppCount = (await Svc<ITokenRepository>().ListApiTokensAsync(Id)).Count;
        return true;
    }

    private void Fill()
    {
        Email = Target.Email;
        FullName = Target.FullName;
        Phone = Target.Phone;
        Address = Target.Address;
        IsSiteAdmin = Target.IsSiteAdmin;
        IsDisabled = Target.IsDisabled;
    }

    private IActionResult Back(string message) => RedirectWith(message, "/Admin/User", new { id = Id });
    private IActionResult BackError(string message) => RedirectWithError(message, "/Admin/User", new { id = Id });

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var admin = await UserAsync();
        var now = DateTime.UtcNow;
        // everything is checked before anything is saved
        var email = Text.NormalizeEmail(Email);
        if (!Text.IsValidEmail(email)) return BackError("Invalid e-mail address.");
        if (Text.Clean(FullName, 120) is not { } name) return BackError("Enter a name.");
        if (Target.Id == admin.Id && (!IsSiteAdmin || IsDisabled))
            return BackError("You cannot remove your own administrator rights or disable yourself here (use another administrator account).");
        if (email != Target.Email && await Users.FindByEmailAsync(email) != null)
            return BackError("Another account uses that e-mail address.");

        if (email != Target.Email) await Users.UpdateEmailAsync(Id, email, now);
        await Users.UpdateProfileAsync(Id, name, Text.Clean(Phone, 40), Text.Clean(Address, 500), now);
        if (IsSiteAdmin != Target.IsSiteAdmin) await Users.SetSiteAdminAsync(Id, IsSiteAdmin, now);
        if (IsDisabled != Target.IsDisabled)
        {
            // a new security stamp ends the browser sessions; the apps' sign-ins are deleted
            await Users.SetDisabledAsync(Id, IsDisabled, Tokens.NewSecurityStamp(), now);
            if (IsDisabled) await Svc<UserCache>().RevokeAllAppTokensAsync(Svc<ITokenRepository>(), Id);
        }
        Svc<UserCache>().Invalidate(Id);
        await Svc<Audit>().WriteAsync("admin.user-updated", $"{email}{(IsSiteAdmin ? " admin" : "")}{(IsDisabled ? " disabled" : "")}", admin.Id);
        return Back("Saved.");
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        if (!await LoadAsync()) return NotFound();
        await Users.SetEmailVerifiedAsync(Id, DateTime.UtcNow);
        Svc<UserCache>().Invalidate(Id);
        await Svc<MembershipService>().NotifyManagersOfPendingRequestsAsync((await Users.GetAsync(Id))!);
        return Back("The e-mail address is marked as confirmed.");
    }

    public async Task<IActionResult> OnPostSendResetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        await Svc<AccountService>().ForgotPasswordAsync(Target.Email);
        return Back($"A password link is on its way to {Target.Email}.");
    }

    public async Task<IActionResult> OnPostShowLinkAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var token = await Svc<UserTokenService>().CreateAsync(Id, TokenPurpose.ResetPassword, TimeSpan.FromHours(24));
        PasswordLink = Svc<SiteUrls>().Absolute("/Account/ResetPassword?token=" + Uri.EscapeDataString(token));
        await Svc<Audit>().WriteAsync("admin.password-link", Target.Email, (await UserAsync()).Id);
        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAppsAsync()
    {
        if (!await LoadAsync()) return NotFound();
        await Svc<UserCache>().RevokeAllAppTokensAsync(Svc<ITokenRepository>(), Id);
        await Svc<Audit>().WriteAsync("admin.apps-signed-out", Target.Email, (await UserAsync()).Id);
        return Back("The apps of this account are signed out.");
    }

    public async Task<IActionResult> OnPostAddMembershipAsync(long orgId, string role)
    {
        if (!await LoadAsync()) return NotFound();
        var admin = await UserAsync();
        try
        {
            var service = Svc<MembershipService>();
            await service.AddMemberAsync(orgId, Target.Email, Target.FullName, role, admin);
            if (role == OrgRoles.Manager) await service.SetRoleAsync(orgId, Id, role, admin);
            return Back("Added.");
        }
        catch (UserFacingException ex) { return BackError(ex.Message); }
    }

    public async Task<IActionResult> OnPostRemoveMembershipAsync(long orgId)
    {
        if (!await LoadAsync()) return NotFound();
        try
        {
            await Svc<MembershipService>().RemoveAsync(orgId, Id, await UserAsync());
            return Back("Removed.");
        }
        catch (UserFacingException ex) { return BackError(ex.Message); }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var admin = await UserAsync();
        if (Target.Id == admin.Id) return BackError("You cannot delete your own account.");
        if (Target.IsSiteAdmin && await Users.CountSiteAdminsAsync() <= 1) return BackError("This is the only administrator.");
        await Users.DeleteAsync(Id);
        Svc<UserCache>().Invalidate(Id);
        await Svc<Audit>().WriteAsync("admin.user-deleted", Target.Email, admin.Id);
        return RedirectWith($"The account of {Target.FullName} was deleted.", "/Admin/Users");
    }
}
