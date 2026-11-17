using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminOrganizationsModel : AdminPageModel
{
    public sealed class InputModel
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "church";
        public string? Description { get; set; }
        public string? ContactEmail { get; set; }
        public bool AllowJoinRequests { get; set; } = true;
        public string? ManagerEmail { get; set; }
        public string? ManagerName { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<OrgRow> Organizations { get; private set; } = new();
    public Dictionary<long, OrgStats> Stats { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var repo = Svc<IOrganizationRepository>();
        Organizations = await repo.ListAsync(includeInactive: true);
        Stats = (await repo.StatsAsync()).ToDictionary(s => s.OrgId);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var repo = Svc<IOrganizationRepository>();
        var name = Text.Clean(Input.Name, 120);
        if (name == null) ModelState.AddModelError("Input.Name", "Enter the name.");
        else if (await repo.FindByNameAsync(name) != null) ModelState.AddModelError("Input.Name", "An organization with this name already exists.");
        if (!OrgKinds.IsValid(Input.Kind)) ModelState.AddModelError("Input.Kind", "Choose a type.");
        if (!string.IsNullOrWhiteSpace(Input.ContactEmail) && !Text.IsValidEmail(Input.ContactEmail)) ModelState.AddModelError("Input.ContactEmail", "Invalid e-mail address.");
        if (!string.IsNullOrWhiteSpace(Input.ManagerEmail) && !Text.IsValidEmail(Input.ManagerEmail)) ModelState.AddModelError("Input.ManagerEmail", "Invalid e-mail address.");
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var admin = await UserAsync();
        var id = await repo.CreateAsync(new OrgRow
        {
            Name = name!, Kind = Input.Kind, Description = Text.Clean(Input.Description, 1000),
            ContactEmail = string.IsNullOrWhiteSpace(Input.ContactEmail) ? null : Text.NormalizeEmail(Input.ContactEmail),
            AllowJoinRequests = Input.AllowJoinRequests, IsActive = true, CreatedAt = DateTime.UtcNow, CreatedBy = admin.Id
        });
        await Svc<Audit>().WriteAsync("org.created", name, admin.Id, id);
        var message = $"“{name}” was created.";
        if (!string.IsNullOrWhiteSpace(Input.ManagerEmail))
        {
            try
            {
                message += " " + await Svc<MembershipService>().AddMemberAsync(id, Input.ManagerEmail, Input.ManagerName, OrgRoles.Manager, admin);
            }
            catch (UserFacingException ex)
            {
                return RedirectWithError($"“{name}” was created, but the manager could not be added: {ex.Message}", "/Admin/Organization", new { id });
            }
        }
        return RedirectWith(message, "/Admin/Organization", new { id });
    }
}
