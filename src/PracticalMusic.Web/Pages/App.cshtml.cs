using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Core.Online;

namespace PracticalMusic.Web.Pages;

/// <summary>The music app (library, player, timing editor, synchronized playback) - see wwwroot/js/app.</summary>
public sealed class AppModel : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Org { get; set; }
    [BindProperty(SupportsGet = true)] public long? Book { get; set; }
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public bool New { get; set; }

    public string BootJson { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        var user = await UserAsync();
        var orgs = await Me.AccessibleOrganizationsAsync();
        var boot = new
        {
            user = new { id = user.Id, name = user.FullName, email = user.Email, isSiteAdmin = user.IsSiteAdmin },
            orgs,
            initial = new
            {
                org = Org is { } o && orgs.Any(x => x.Id == o) ? o : orgs.FirstOrDefault()?.Id,
                book = Book,
                view = View is "library" or "player" or "share" or "about" or "timing" ? View : null,
                newBook = New
            },
            siteName = Site.Name,
            maxUploadMb = Site.MaxUploadMegabytes,
            syncProtocol = SyncWireMessage.ProtocolVersion
        };
        // Safe inside <script type="application/json">: the encoder escapes <, >, & and quotes.
        BootJson = JsonSerializer.Serialize(boot, new JsonSerializerOptions(OnlineJson.Options) { Encoder = JavaScriptEncoder.Default });
    }
}
