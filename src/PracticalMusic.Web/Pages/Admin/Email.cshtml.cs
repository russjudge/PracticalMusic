using Microsoft.AspNetCore.Mvc;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Services;

namespace PracticalMusic.Web.Pages.Admin;

public sealed class AdminEmailModel : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public string Transport { get; private set; } = "";
    public string AdminEmail { get; private set; } = "";
    public List<EmailRow> Emails { get; private set; } = new();
    public Dictionary<string, int> Counts { get; private set; } = new();
    public string? TestResult { get; private set; }
    public bool TestOk { get; private set; }

    private async Task LoadAsync()
    {
        Transport = Svc<IMailTransport>().Describe();
        AdminEmail = (await UserAsync()).Email;
        var outbox = Svc<IEmailOutboxRepository>();
        var status = Status is "queued" or "failed" or "sent" ? Status : null;
        Emails = await outbox.ListRecentAsync(200, status);
        Counts = await outbox.CountByStatusAsync();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostTestAsync(string? to)
    {
        await LoadAsync();
        var address = Text.NormalizeEmail(to);
        if (!Text.IsValidEmail(address))
        {
            ModelState.AddModelError(string.Empty, "Enter a valid e-mail address.");
            return Page();
        }
        var content = EmailTemplates.Test(Site.Name, address);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Svc<IMailTransport>().SendAsync(new EmailRow { ToAddress = address, Subject = content.Subject, BodyText = content.Text, BodyHtml = content.Html }, cts.Token);
            TestOk = true;
            TestResult = $"Accepted for delivery to {address}. If it does not arrive, look in the spam folder and check SPF/DKIM (docs/EMAIL-SETUP.md).";
        }
        catch (Exception ex)
        {
            TestResult = "Sending failed: " + ex.Message + (ex.InnerException != null ? "\n" + ex.InnerException.Message : "");
        }
        await Svc<Audit>().WriteAsync("admin.email-test", $"{address}: {(TestOk ? "ok" : TestResult)}", (await UserAsync()).Id);
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync(long id)
    {
        await Svc<IEmailOutboxRepository>().RetryAsync(id, DateTime.UtcNow);
        Svc<EmailSignal>().Notify();
        return RedirectWith("The message is queued again.", "/Admin/Email");
    }
}
