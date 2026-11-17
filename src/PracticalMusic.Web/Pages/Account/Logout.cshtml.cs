using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Security;

namespace PracticalMusic.Web.Pages.Account;

public sealed class LogoutModel : AppPageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(AuthSchemes.Cookie);
        return RedirectWith("You are signed out.", "/Index");
    }
}
