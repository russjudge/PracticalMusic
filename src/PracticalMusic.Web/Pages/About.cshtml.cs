using Microsoft.AspNetCore.Authorization;

namespace PracticalMusic.Web.Pages;

[AllowAnonymous]
public sealed class AboutModel : AppPageModel
{
    public string Version => Api.ApiEndpoints.Version;
    public string OriginalPrompt { get; private set; } = "";
    public string WebsitePrompt { get; private set; } = "";

    public void OnGet()
    {
        OriginalPrompt = Resource("PracticalMusic.Web.original-prompt.txt");
        WebsitePrompt = Resource("PracticalMusic.Web.website-prompt.txt");
    }

    private static string Resource(string name)
    {
        using var stream = typeof(AboutModel).Assembly.GetManifestResourceStream(name);
        if (stream == null) return "(not available)";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
