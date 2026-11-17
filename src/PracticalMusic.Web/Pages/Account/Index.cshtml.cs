using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Account;

public sealed class AccountIndexModel : AppPageModel
{
    [BindProperty] public string FullName { get; set; } = "";
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? Address { get; set; }
    public string Email { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var user = await UserAsync();
        Email = user.Email;
        FullName = user.FullName;
        Phone = user.Phone;
        Address = user.Address;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await UserAsync();
        Email = user.Email;
        if (!await TryAsync(() => Svc<AccountService>().UpdateProfileAsync(user.Id, FullName, Phone, Address, user.Id))) return Page();
        return RedirectWith("Your details are saved.", "/Account/Index");
    }
}
