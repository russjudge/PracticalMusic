using Microsoft.AspNetCore.Authorization;

namespace PracticalMusic.Web.Pages;

[AllowAnonymous]
public sealed class AccessDeniedModel : AppPageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
