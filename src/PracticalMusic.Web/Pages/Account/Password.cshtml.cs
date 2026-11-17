using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

public sealed class PasswordModel : AppPageModel
{
    [BindProperty] public string Current { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    [BindProperty] public bool SignOutApps { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await UserAsync();
        if (NewPassword != Confirm)
        {
            ModelState.AddModelError(nameof(Confirm), "The two new passwords differ.");
            return Page();
        }
        try
        {
            var updated = await Svc<AccountService>().ChangePasswordAsync(user.Id, Current, NewPassword, SignOutApps);
            // the security stamp changed: issue a fresh cookie for this browser
            await HttpContext.SignInAsync(AuthSchemes.Cookie, AuthSetup.CookiePrincipal(updated), new AuthenticationProperties { IsPersistent = true });
            return RedirectWith("Your password was changed.", "/Account/Index");
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
