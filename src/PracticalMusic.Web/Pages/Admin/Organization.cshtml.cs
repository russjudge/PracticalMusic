using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminOrganizationModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Kind { get; set; } = "church";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? ContactEmail { get; set; }
    [BindProperty] public bool AllowJoinRequests { get; set; }
    [BindProperty] public bool IsActive { get; set; }

    public OrgRow Org { get; private set; } = new();
    public List<UserRow> Managers { get; private set; } = new();

    private async Task<bool> LoadAsync()
    {
        var org = await Svc<IOrganizationRepository>().GetAsync(Id);
        if (org == null) return false;
        Org = org;
        Managers = await Svc<IOrganizationRepository>().ListActiveManagersAsync(Id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        Name = Org.Name;
        Kind = Org.Kind;
        Description = Org.Description;
        ContactEmail = Org.ContactEmail;
        AllowJoinRequests = Org.AllowJoinRequests;
        IsActive = Org.IsActive;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return NotFound();
        var repo = Svc<IOrganizationRepository>();
        var name = Text.Clean(Name, 120);
        if (name == null) ModelState.AddModelError(nameof(Name), "Enter the name.");
        else if (await repo.FindByNameAsync(name) is { } other && other.Id != Id) ModelState.AddModelError(nameof(Name), "Another organization has this name.");
        if (!OrgKinds.IsValid(Kind)) ModelState.AddModelError(nameof(Kind), "Choose a type.");
        if (!string.IsNullOrWhiteSpace(ContactEmail) && !Text.IsValidEmail(ContactEmail)) ModelState.AddModelError(nameof(ContactEmail), "Invalid e-mail address.");
        if (!ModelState.IsValid) return Page();
        Org.Name = name!;
        Org.Kind = Kind;
        Org.Description = Text.Clean(Description, 1000);
        Org.ContactEmail = string.IsNullOrWhiteSpace(ContactEmail) ? null : Text.NormalizeEmail(ContactEmail);
        Org.AllowJoinRequests = AllowJoinRequests;
        Org.IsActive = IsActive;
        await repo.UpdateAsync(Org);
        foreach (var member in await repo.ListMembersAsync(Id)) Svc<Security.UserCache>().Invalidate(member.UserId);
        await Svc<Audit>().WriteAsync("org.updated", $"{Org.Name}{(IsActive ? "" : " (inactive)")}", (await UserAsync()).Id, Id);
        return RedirectWith("Saved.", "/Admin/Organization", new { id = Id });
    }

    public async Task<IActionResult> OnPostAddManagerAsync(string? email, string? fullName)
    {
        if (!await LoadAsync()) return NotFound();
        var admin = await UserAsync();
        try
        {
            var service = Svc<MembershipService>();
            var message = await service.AddMemberAsync(Id, email, fullName, OrgRoles.Manager, admin);
            // someone who was already a member is promoted
            var user = await Svc<IUserRepository>().FindByEmailAsync(email ?? "");
            if (user != null) await service.SetRoleAsync(Id, user.Id, OrgRoles.Manager, admin);
            return RedirectWith(message, "/Admin/Organization", new { id = Id });
        }
        catch (UserFacingException ex)
        {
            return RedirectWithError(ex.Message, "/Admin/Organization", new { id = Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? confirmName)
    {
        if (!await LoadAsync()) return NotFound();
        if (!string.Equals(confirmName?.Trim(), Org.Name, StringComparison.Ordinal))
            return RedirectWithError("The name did not match; nothing was deleted.", "/Admin/Organization", new { id = Id });
        var repo = Svc<IOrganizationRepository>();
        var members = await repo.ListMembersAsync(Id);
        await repo.DeleteAsync(Id);
        foreach (var m in members) Svc<Security.UserCache>().Invalidate(m.UserId);
        await Svc<Audit>().WriteAsync("org.deleted", Org.Name, (await UserAsync()).Id, Id);
        return RedirectWith($"“{Org.Name}” was deleted.", "/Admin/Organizations");
    }
}
