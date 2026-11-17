using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PracticalMusic.Web.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : AppPageModel
{
    public string? RequestId { get; private set; }

    public void OnGet() => RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    public void OnPost() => OnGet();
}
