using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminIndexModel : AdminPageModel
{
    public int Organizations { get; private set; }
    public int Users { get; private set; }
    public int Books { get; private set; }
    public int Connections { get; private set; }
    public int EmailQueued { get; private set; }
    public int EmailFailed { get; private set; }
    public string EmailTransport { get; private set; } = "";
    public string Database { get; private set; } = "";
    public string DataFolder { get; private set; } = "";
    public List<AuditRow> Audit { get; private set; } = new();
    public Dictionary<long, string> OrgNames { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var orgs = await Svc<IOrganizationRepository>().ListAsync(includeInactive: true);
        Organizations = orgs.Count;
        OrgNames = orgs.ToDictionary(o => o.Id, o => o.Name);
        Users = await Svc<IUserRepository>().CountAsync();
        Books = await Svc<IBookRepository>().CountAsync();
        Connections = Svc<SyncHub>().ConnectionCount;
        var counts = await Svc<IEmailOutboxRepository>().CountByStatusAsync();
        EmailQueued = counts.GetValueOrDefault(EmailStatus.Queued);
        EmailFailed = counts.GetValueOrDefault(EmailStatus.Failed);
        EmailTransport = Svc<IMailTransport>().Describe();
        Database = Svc<ISchemaInfo>().Describe();
        DataFolder = Svc<DataPaths>().Root;
        Audit = await Svc<IAuditRepository>().RecentAsync(100);
    }
}
