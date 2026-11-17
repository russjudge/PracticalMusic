using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class RegisterModel : AppPageModel
{
    public sealed class InputModel
    {
        public long? OrgId { get; set; }
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string Password { get; set; } = "";
        public string ConfirmPassword { get; set; } = "";
        public string? Message { get; set; }
        /// <summary>Honeypot: people never fill it in, form-filling robots do.</summary>
        public string? Website { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<OrgRow> Organizations { get; private set; } = new();
    public bool Done { get; private set; }
    public bool VerificationRequired => Site.RequireEmailVerification;

    public async Task OnGetAsync([FromQuery] long? org)
    {
        Organizations = await Svc<IOrganizationRepository>().ListJoinableAsync();
        if (org != null && Organizations.Any(o => o.Id == org)) Input.OrgId = org;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Organizations = await Svc<IOrganizationRepository>().ListJoinableAsync();
        if (!string.IsNullOrEmpty(Input.Website))
        {
            Done = true; // pretend it worked
            return Page();
        }
        if (Input.OrgId is not { } orgId || Organizations.All(o => o.Id != orgId)) ModelState.AddModelError("Input.OrgId", "Choose an organization.");
        if (string.IsNullOrWhiteSpace(Input.FullName)) ModelState.AddModelError("Input.FullName", "Enter your name.");
        if (!Text.IsValidEmail(Input.Email)) ModelState.AddModelError("Input.Email", "Enter a valid e-mail address.");
        if (PasswordHashing.Validate(Input.Password, Input.Email) is { } problem) ModelState.AddModelError("Input.Password", problem);
        else if (Input.Password != Input.ConfirmPassword) ModelState.AddModelError("Input.ConfirmPassword", "The two passwords differ.");
        if (!ModelState.IsValid) return Page();

        if (!await TryAsync(() => Svc<AccountService>().RegisterAsync(new RegisterInput
            {
                OrgId = Input.OrgId!.Value, FullName = Input.FullName, Email = Input.Email, Phone = Input.Phone, Address = Input.Address,
                Password = Input.Password, Message = Input.Message
            })))
            return Page();
        Done = true;
        return Page();
    }
}
