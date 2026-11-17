using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Pages;

public sealed class IndexModel : AppPageModel
{
    public bool SignedIn { get; private set; }
    public string UserName { get; private set; } = "";
    public List<OrgMembershipInfo> Organizations { get; private set; } = new();
    public List<UserOrgRow> Pending { get; private set; } = new();
    public List<UserOrgRow> Invitations { get; private set; } = new();
    public Dictionary<long, int> PendingCounts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var user = await Me.GetAsync();
        if (user == null) return;
        SignedIn = true;
        UserName = user.FullName;
        Organizations = await Me.AccessibleOrganizationsAsync();
        var orgs = Svc<IOrganizationRepository>();
        var mine = await orgs.ListForUserAsync(user.Id);
        Pending = mine.Where(m => m.Status == MembershipStatus.Pending && m.IsActive).ToList();
        Invitations = mine.Where(m => m.Status == MembershipStatus.Invited && m.IsActive).ToList();
        var managed = Organizations.Where(o => o.CanEdit).Select(o => o.Id).ToHashSet();
        if (managed.Count > 0)
            PendingCounts = (await orgs.StatsAsync()).Where(s => managed.Contains(s.OrgId)).ToDictionary(s => s.OrgId, s => s.Pending);
    }
}
