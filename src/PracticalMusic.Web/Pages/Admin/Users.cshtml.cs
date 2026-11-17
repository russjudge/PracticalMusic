using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminUsersModel : AdminPageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageIndex { get; set; }

    public List<UserRow> Users { get; private set; } = new();
    public int Total { get; private set; }

    public async Task OnGetAsync()
    {
        PageIndex = Math.Max(0, PageIndex);
        (Users, Total) = await Svc<IUserRepository>().SearchAsync(Q, PageIndex * PageSize, PageSize);
    }
}
