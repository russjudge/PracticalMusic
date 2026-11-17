using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PracticalMusic.Web.Pages;

/// <summary>Friendly pages for 403/404/500 … (status code pages and the exception handler land here).</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class StatusModel : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int Code { get; set; }

    public string Heading { get; private set; } = "";
    public string Explanation { get; private set; } = "";

    public void OnGet() => Fill();
    public void OnPost() => Fill();

    private void Fill()
    {
        if (Code == 0) Code = 500;
        (Heading, Explanation) = Code switch
        {
            403 => ("Not allowed", "You do not have permission to open this page. If you think you should, ask a manager of your organization."),
            404 => ("Not found", "This page does not exist, or you do not have access to it."),
            429 => ("Slow down", "Too many attempts in a short time. Wait a minute and try again."),
            >= 500 => ("Something went wrong", "The website could not complete the request. Please try again; if it keeps happening, tell the administrator" +
                                                  (Activity.Current?.Id is { } id ? $" (reference {id})." : ".")),
            _ => ($"Error {Code}", "The request could not be completed.")
        };
    }
}
