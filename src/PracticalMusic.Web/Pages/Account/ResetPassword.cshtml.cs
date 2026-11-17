using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ResetPasswordModel : AppPageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    public bool LinkValid { get; private set; }
    public bool IsInvite { get; private set; }
    public string Email { get; private set; } = "";

    public async Task OnGetAsync() => await PeekAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PeekAsync()) return Page();
        if (Password != ConfirmPassword)
        {
            ModelState.AddModelError(nameof(ConfirmPassword), "The two passwords differ.");
            return Page();
        }
        var accounts = Svc<AccountService>();
        try
        {
            var user = await accounts.ResetPasswordAsync(Token, Password);
            await HttpContext.SignOutAsync(AuthSchemes.Cookie);
            await HttpContext.SignInAsync(AuthSchemes.Cookie, AuthSetup.CookiePrincipal(user), new AuthenticationProperties { IsPersistent = true });
            return RedirectWith(IsInvite ? "Your password is set. Welcome to Practical Music!" : "Your new password is saved.", "/Index");
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    private async Task<bool> PeekAsync()
    {
        var peek = await Svc<AccountService>().PeekResetTokenAsync(Token);
        if (peek == null) return false;
        LinkValid = true;
        IsInvite = peek.Value.IsInvite;
        Email = peek.Value.User.Email;
        return true;
    }
}
