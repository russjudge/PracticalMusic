using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class VerifyEmailModel : AppPageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    public bool Verified { get; private set; }
    public string Email { get; private set; } = "";
    public bool HasPendingRequests { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await Svc<AccountService>().VerifyEmailAsync(Token);
        if (user == null) return;
        Verified = true;
        Email = user.Email;
        HasPendingRequests = (await Svc<IOrganizationRepository>().ListForUserAsync(user.Id)).Any(m => m.Status == MembershipStatus.Pending);
    }
}
