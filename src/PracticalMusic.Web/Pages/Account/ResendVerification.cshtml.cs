using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ResendVerificationModel : AppPageModel
{
    [BindProperty] public string Email { get; set; } = "";
    public bool Sent { get; private set; }

    public void OnGet() { }

    public async Task OnPostAsync()
    {
        if (Text.IsValidEmail(Email)) await Svc<AccountService>().ResendVerificationAsync(Email);
        Sent = true;
    }
}
