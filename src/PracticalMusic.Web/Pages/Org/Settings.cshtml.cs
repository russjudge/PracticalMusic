using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Org;

public sealed class OrgSettingsModel : OrgPageModel
{
    protected override bool RequireManager => true;

    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? ContactEmail { get; set; }
    [BindProperty] public bool AllowJoinRequests { get; set; }

    public void OnGet()
    {
        Description = Org.Description;
        ContactEmail = Org.ContactEmail;
        AllowJoinRequests = Org.AllowJoinRequests;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!string.IsNullOrWhiteSpace(ContactEmail) && !Text.IsValidEmail(ContactEmail))
        {
            ModelState.AddModelError(nameof(ContactEmail), "Enter a valid e-mail address or leave it empty.");
            return Page();
        }
        Org.Description = Text.Clean(Description, 1000);
        Org.ContactEmail = string.IsNullOrWhiteSpace(ContactEmail) ? null : Text.NormalizeEmail(ContactEmail);
        Org.AllowJoinRequests = AllowJoinRequests;
        await Svc<IOrganizationRepository>().UpdateAsync(Org);
        await Svc<Audit>().WriteAsync("org.settings", null, (await UserAsync()).Id, Org.Id);
        return RedirectWith("Saved.", "/Org/Index", new { orgId = Org.Id });
    }
}
