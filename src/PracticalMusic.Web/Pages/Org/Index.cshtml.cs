using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Org;

public sealed class OrgIndexModel : OrgPageModel
{
    public List<BookRow> Books { get; private set; } = new();
    public int PendingCount { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Books = await Svc<IBookRepository>().ListForOrgAsync(Org.Id);
        if (IsManager) PendingCount = (await Svc<IOrganizationRepository>().StatsAsync()).FirstOrDefault(s => s.OrgId == Org.Id)?.Pending ?? 0;
    }

    // .pmbook uploads go from the page (site.js) to POST /api/orgs/{orgId}/books/package: large bodies are only
    // accepted there, after the manager check.

    public async Task<IActionResult> OnPostDeleteAsync(long bookId)
    {
        if (!IsManager) return Forbid();
        var books = Svc<IBookRepository>();
        var book = await books.GetAsync(bookId);
        if (book == null || book.OrgId != Org.Id) return NotFound();
        await books.DeleteAsync(bookId);
        await Svc<Audit>().WriteAsync("book.deleted", $"\"{book.Title}\"", (await UserAsync()).Id, Org.Id);
        return RedirectWith($"“{book.Title}” was deleted.", "/Org/Index", new { orgId = Org.Id });
    }
}
