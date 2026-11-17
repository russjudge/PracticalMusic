using Microsoft.AspNetCore.Authorization;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages;

[AllowAnonymous]
public sealed class DownloadsModel : AppPageModel
{
    public List<DownloadsInfo.Item> Items { get; private set; } = new();
    public bool Visible { get; private set; }
    public string Folder { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var paths = Svc<DataPaths>();
        Folder = paths.Downloads;
        Visible = Site.PublicDownloads || await Me.GetAsync() != null;
        if (Visible) Items = DownloadsInfo.List(paths);
    }
}
