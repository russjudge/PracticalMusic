using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

public sealed class DevicesModel : AppPageModel
{
    public List<ApiTokenRow> Tokens { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var user = await UserAsync();
        Tokens = await Svc<ITokenRepository>().ListApiTokensAsync(user.Id);
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        var user = await UserAsync();
        var tokens = Svc<ITokenRepository>();
        var row = (await tokens.ListApiTokensAsync(user.Id)).FirstOrDefault(t => t.Id == id);
        if (row != null)
        {
            await tokens.DeleteApiTokenAsync(id, user.Id);
            Svc<UserCache>().InvalidateToken(row.TokenHash);
            await Svc<Audit>().WriteAsync("app.signout", row.DeviceName, user.Id);
        }
        return RedirectWith("The device is signed out.", "/Account/Devices");
    }

    public async Task<IActionResult> OnPostRevokeAllAsync()
    {
        var user = await UserAsync();
        await Svc<UserCache>().RevokeAllAppTokensAsync(Svc<ITokenRepository>(), user.Id);
        await Svc<Audit>().WriteAsync("app.signout-all", null, user.Id);
        return RedirectWith("All apps are signed out.", "/Account/Devices");
    }
}
