using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Org;

public sealed class OrgMemberModel : OrgPageModel
{
    protected override bool RequireManager => true;

    [BindProperty(SupportsGet = true)] public long UserId { get; set; }
    [BindProperty] public string FullName { get; set; } = "";
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string MemberRole { get; set; } = "member";

    public UserRow Member { get; private set; } = new();
    public MembershipRow Membership { get; private set; } = new();
    /// <summary>Organization managers may not edit the website administrator.</summary>
    public bool ReadOnly { get; private set; }

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        await base.OnPageHandlerExecutionAsync(context, async () =>
        {
            var member = await Svc<IUserRepository>().GetAsync(UserId);
            var membership = member == null ? null : await Svc<IOrganizationRepository>().GetMembershipAsync(member.Id, Org.Id);
            // only people who have access: a waiting request is handled on the Members page, and an invitation
            // that was not accepted yet does not let managers see or change the person's account
            if (member == null || membership is not { Status: MembershipStatus.Active })
            {
                context.Result = NotFound();
                return null!;
            }
            Member = member;
            Membership = membership;
            ReadOnly = member.IsSiteAdmin && !IsSiteAdmin;
            return await next();
        });
    }

    public void OnGet()
    {
        FullName = Member.FullName;
        Phone = Member.Phone;
        Address = Member.Address;
        MemberRole = Membership.Role;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (ReadOnly) return Forbid();
        var actor = await UserAsync();
        try
        {
            await Svc<AccountService>().UpdateProfileAsync(Member.Id, FullName, Phone, Address, actor.Id, Org.Id);
            if (Membership.Status == MembershipStatus.Active && MemberRole != Membership.Role)
                await Svc<MembershipService>().SetRoleAsync(Org.Id, Member.Id, MemberRole, actor);
            return RedirectWith($"{FullName} was saved.", "/Org/Members", new { orgId = Org.Id });
        }
        catch (UserFacingException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSendResetAsync()
    {
        if (ReadOnly) return Forbid();
        if (Member.HasPassword) await Svc<AccountService>().ForgotPasswordAsync(Member.Email);
        else await Svc<MembershipService>().SendInvitationAsync(Member, Org, await UserAsync());
        return RedirectWith($"An e-mail is on its way to {Member.Email}.", "/Org/Member", new { orgId = Org.Id, userId = Member.Id });
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        if (ReadOnly) return Forbid();
        try
        {
            await Svc<MembershipService>().RemoveAsync(Org.Id, Member.Id, await UserAsync());
            return RedirectWith($"{Member.FullName} was removed from {Org.Name}.", "/Org/Members", new { orgId = Org.Id });
        }
        catch (UserFacingException ex)
        {
            return RedirectWithError(ex.Message, "/Org/Member", new { orgId = Org.Id, userId = Member.Id });
        }
    }
}
