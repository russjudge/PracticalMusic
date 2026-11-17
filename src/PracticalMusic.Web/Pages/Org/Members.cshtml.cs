using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Org;

public sealed class OrgMembersModel : OrgPageModel
{
    protected override bool RequireManager => true;

    public List<MemberRow> Members { get; private set; } = new();

    public async Task OnGetAsync() => Members = await Svc<IOrganizationRepository>().ListMembersAsync(Org.Id);

    private async Task<IActionResult> Run(Func<UserRow, Task<string>> action)
    {
        var actor = await UserAsync();
        try
        {
            var message = await action(actor);
            return RedirectWith(message, "/Org/Members", new { orgId = Org.Id });
        }
        catch (UserFacingException ex)
        {
            return RedirectWithError(ex.Message, "/Org/Members", new { orgId = Org.Id });
        }
    }

    /// <summary>How a person is named in messages: their e-mail address while an invitation is not accepted (their account is not visible to managers yet).</summary>
    private async Task<string> NameOf(long userId)
    {
        var user = await Svc<IUserRepository>().GetAsync(userId);
        if (user == null) return "The person";
        var membership = await Svc<IOrganizationRepository>().GetMembershipAsync(userId, Org.Id);
        return membership?.Status == MembershipStatus.Invited ? user.Email : user.FullName;
    }

    public Task<IActionResult> OnPostApproveAsync(long userId, string role) => Run(async actor =>
    {
        await Svc<MembershipService>().ApproveAsync(Org.Id, userId, role, actor);
        return $"{await NameOf(userId)} now has access{(role == "manager" ? " as a manager" : "")} and was told by e-mail.";
    });

    public Task<IActionResult> OnPostDeclineAsync(long userId) => Run(async actor =>
    {
        await Svc<MembershipService>().RejectAsync(Org.Id, userId, actor, notify: true);
        return $"The request of {await NameOf(userId)} was declined.";
    });

    public Task<IActionResult> OnPostRoleAsync(long userId, string role) => Run(async actor =>
    {
        await Svc<MembershipService>().SetRoleAsync(Org.Id, userId, role, actor);
        return $"{await NameOf(userId)} is now a {(role == "manager" ? "manager" : "member")}.";
    });

    public Task<IActionResult> OnPostRemoveAsync(long userId) => Run(async actor =>
    {
        var name = await NameOf(userId);
        var invitation = (await Svc<IOrganizationRepository>().GetMembershipAsync(userId, Org.Id))?.Status == MembershipStatus.Invited;
        var target = await Svc<IUserRepository>().GetAsync(userId);
        if (target is { IsSiteAdmin: true } && !actor.IsSiteAdmin && !invitation)
            throw UserFacingException.Forbidden("The website administrator cannot be removed by an organization manager.");
        await Svc<MembershipService>().RemoveAsync(Org.Id, userId, actor);
        return invitation ? $"The invitation for {name} was withdrawn." : $"{name} was removed from {Org.Name}.";
    });

    public Task<IActionResult> OnPostAddAsync(string? email, string? fullName, string role) =>
        Run(actor => Svc<MembershipService>().AddMemberAsync(Org.Id, email, fullName, role, actor));
}
