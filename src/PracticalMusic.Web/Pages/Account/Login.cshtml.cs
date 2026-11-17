using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class LoginModel : AppPageModel
{
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public bool NeedsVerification { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await Me.GetAsync() != null) return LocalRedirect(SafeReturnUrl());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await Svc<AccountService>().CheckPasswordAsync(Email, Password);
        switch (result.Status)
        {
            case LoginStatus.Success:
                await HttpContext.SignInAsync(AuthSchemes.Cookie, AuthSetup.CookiePrincipal(result.User!), new AuthenticationProperties
                {
                    IsPersistent = RememberMe,
                    ExpiresUtc = RememberMe ? DateTimeOffset.UtcNow.AddDays(30) : null,
                    AllowRefresh = true
                });
                return LocalRedirect(SafeReturnUrl());
            case LoginStatus.NotVerified:
                NeedsVerification = true;
                break;
            case LoginStatus.Disabled:
                ModelState.AddModelError(string.Empty, result.Message ?? "You cannot sign in right now.");
                break;
            default: // wrong password, unknown address or a temporary lock: the same answer
                ModelState.AddModelError(string.Empty, AccountService.LoginFailedMessage);
                break;
        }
        Password = "";
        return Page();
    }

    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/Account/Log", StringComparison.OrdinalIgnoreCase) ? ReturnUrl : "/";
}
