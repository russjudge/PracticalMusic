using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using Xunit;

namespace PracticalMusic.Web.Tests;

[Collection("web")]
public sealed class AccountFlowTests(WebFixture web)
{
    [Fact]
    public async Task Public_pages_render()
    {
        var client = web.BrowserClient();
        foreach (var url in new[] { "/", "/About", "/Downloads", "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/manifest.webmanifest", "/js/app/main.js", "/css/app.css", "/healthz" })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}");
        }
        var about = await client.GetStringAsync("/About");
        Assert.Contains("Build a website that the Linux, Windows, and Android apps can work with", about); // the website prompt
        Assert.Contains("Create an application that will run on Windows, Linux, and Android", about);     // the original prompt
        var missing = await client.GetAsync("/no/such/page");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Security_headers_are_sent()
    {
        var response = await web.BrowserClient().GetAsync("/");
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Private_pages_need_sign_in_and_admin_pages_need_the_administrator()
    {
        var anonymous = web.BrowserClient();
        var app = await anonymous.GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, app.StatusCode);
        Assert.StartsWith("/Account/Login", app.Headers.Location!.PathAndQuery);

        var user = await web.CreateUserAsync();
        var browser = await web.SignedInBrowserAsync(user.Email);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/App")).StatusCode);
        var admin = await browser.GetAsync("/Admin");
        Assert.NotEqual(HttpStatusCode.OK, admin.StatusCode);

        var siteAdmin = await web.CreateUserAsync(admin: true);
        var adminBrowser = await web.SignedInBrowserAsync(siteAdmin.Email);
        foreach (var url in new[] { "/Admin", "/Admin/Organizations", "/Admin/Users", "/Admin/Email", $"/Admin/User/{user.Id}" })
            Assert.True((await adminBrowser.GetAsync(url)).StatusCode == HttpStatusCode.OK, url);
    }

    [Fact]
    public async Task Sign_up_confirm_approve_and_get_access()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync();
        await web.AddMemberAsync(manager.Id, org.Id, "manager");

        // 1. sign up, choosing the organization from the drop-down
        var email = WebFixture.Unique("singer") + "@example.com";
        var visitor = web.BrowserClient();
        Assert.Contains(org.Name, await visitor.GetStringAsync("/Account/Register"));
        var signUp = await WebFixture.PostFormAsync(visitor, "/Account/Register", new()
        {
            ["Input.OrgId"] = org.Id.ToString(), ["Input.FullName"] = "Sam Singer", ["Input.Email"] = email, ["Input.Phone"] = "555-0100",
            ["Input.Address"] = "1 Choir Lane", ["Input.Password"] = "a long enough password", ["Input.ConfirmPassword"] = "a long enough password",
            ["Input.Message"] = "I sing tenor."
        });
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        Assert.Contains("Almost there", await signUp.Content.ReadAsStringAsync());

        // not confirmed yet: no sign-in, managers not bothered yet
        var early = await WebFixture.PostFormAsync(web.BrowserClient(), "/Account/Login", new() { ["Email"] = email, ["Password"] = "a long enough password" });
        Assert.Contains("confirm your e-mail address", await early.Content.ReadAsStringAsync());
        Assert.Null(await web.LastEmailToAsync(manager.Email));

        // 2. confirm the address → the manager is told
        var verify = await web.LastEmailToAsync(email);
        Assert.NotNull(verify);
        Assert.Contains("Confirm", verify!.Subject);
        var verified = await visitor.GetStringAsync(WebFixture.LinkWithToken(verify, "/Account/VerifyEmail"));
        Assert.Contains("is confirmed", verified);
        var notice = await web.LastEmailToAsync(manager.Email);
        Assert.NotNull(notice);
        Assert.Contains("Sam Singer", notice!.BodyText);
        Assert.Contains("555-0100", notice.BodyText);
        Assert.Contains("I sing tenor.", notice.BodyText);

        // 3. signed in, but no access yet
        var singer = await web.SignedInBrowserAsync(email, "a long enough password");
        Assert.Contains("Waiting for approval", await singer.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await singer.GetAsync($"/Org/{org.Id}")).StatusCode);

        // 4. the manager approves on the Members page
        var managerBrowser = await web.SignedInBrowserAsync(manager.Email);
        var membersPage = await managerBrowser.GetStringAsync($"/Org/{org.Id}/Members");
        Assert.Contains("Sam Singer", membersPage);
        var user = (await web.Service<IUserRepository>().FindByEmailAsync(email))!;
        var approve = await WebFixture.PostFormAsync(managerBrowser, $"/Org/{org.Id}/Members?handler=Approve",
            new() { ["userId"] = user.Id.ToString(), ["role"] = "member" }, $"/Org/{org.Id}/Members");
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);

        // 5. the e-mail that says so, and the access
        var granted = await web.LastEmailToAsync(email);
        Assert.Contains("You now have access", granted!.Subject);
        Assert.Equal(HttpStatusCode.OK, (await singer.GetAsync($"/Org/{org.Id}")).StatusCode);
        // members cannot manage
        Assert.NotEqual(HttpStatusCode.OK, (await singer.GetAsync($"/Org/{org.Id}/Members")).StatusCode);
    }

    [Fact]
    public async Task Signing_up_with_a_known_address_reveals_nothing_and_tells_the_owner()
    {
        var org = await web.CreateOrgAsync();
        var existing = await web.CreateUserAsync();
        var response = await WebFixture.PostFormAsync(web.BrowserClient(), "/Account/Register", new()
        {
            ["Input.OrgId"] = org.Id.ToString(), ["Input.FullName"] = "Somebody Else", ["Input.Email"] = existing.Email,
            ["Input.Password"] = "another long password", ["Input.ConfirmPassword"] = "another long password"
        });
        Assert.Contains("Almost there", await response.Content.ReadAsStringAsync());
        var mail = await web.LastEmailToAsync(existing.Email);
        Assert.Contains("already have an account", mail!.Subject);
        var user = await web.Service<IUserRepository>().FindByEmailAsync(existing.Email);
        Assert.Equal(existing.FullName, user!.FullName); // unchanged
    }

    [Fact]
    public async Task Forgot_password_resets_it_and_signs_out_other_sessions()
    {
        var user = await web.CreateUserAsync(password: "the old password");
        var oldSession = await web.SignedInBrowserAsync(user.Email, "the old password");
        Assert.Equal(HttpStatusCode.OK, (await oldSession.GetAsync("/Account")).StatusCode);

        var forgot = await WebFixture.PostFormAsync(web.BrowserClient(), "/Account/ForgotPassword", new() { ["Email"] = user.Email });
        Assert.Contains("If an account exists", await forgot.Content.ReadAsStringAsync());
        var mail = await web.LastEmailToAsync(user.Email);
        var link = WebFixture.LinkWithToken(mail!, "/Account/ResetPassword");

        var browser = web.BrowserClient();
        var reset = await WebFixture.PostFormAsync(browser, link, new() { ["Password"] = "the brand new password", ["ConfirmPassword"] = "the brand new password" });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode); // signed in by the reset

        // the link works once; the old password no longer does; the old session is gone
        Assert.Contains("does not work", await web.BrowserClient().GetStringAsync(link));
        var oldLogin = await WebFixture.PostFormAsync(web.BrowserClient(), "/Account/Login", new() { ["Email"] = user.Email, ["Password"] = "the old password" });
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode); // the page again, with an error
        Assert.Equal(HttpStatusCode.Redirect, (await oldSession.GetAsync("/Account")).StatusCode);
        await web.SignedInBrowserAsync(user.Email, "the brand new password");
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_for_a_while()
    {
        var user = await web.CreateUserAsync(password: "right password here");
        var accounts = web.Services.CreateScope().ServiceProvider.GetRequiredService<PracticalMusic.Web.Services.AccountService>();
        for (var i = 0; i < PracticalMusic.Web.Services.AccountService.MaxFailedLogins; i++)
            await accounts.CheckPasswordAsync(user.Email, "wrong");
        var result = await accounts.CheckPasswordAsync(user.Email, "right password here");
        Assert.Equal(PracticalMusic.Web.Services.LoginStatus.Locked, result.Status);
    }

    [Fact]
    public async Task Managers_add_people_and_keep_at_least_one_manager()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync();
        await web.AddMemberAsync(manager.Id, org.Id, "manager");
        var browser = await web.SignedInBrowserAsync(manager.Email);

        // a new person gets an invitation to choose a password
        var invitee = WebFixture.Unique("new") + "@example.com";
        await WebFixture.PostFormAsync(browser, $"/Org/{org.Id}/Members?handler=Add",
            new() { ["email"] = invitee, ["fullName"] = "Nina New", ["role"] = "member" }, $"/Org/{org.Id}/Members");
        var invitation = await web.LastEmailToAsync(invitee);
        Assert.Contains("invited", invitation!.Subject);
        var link = WebFixture.LinkWithToken(invitation, "/Account/ResetPassword");
        Assert.Contains("Choose your password", await web.BrowserClient().GetStringAsync(link));

        // the only manager cannot demote or remove themselves
        var demote = await WebFixture.PostFormAsync(browser, $"/Org/{org.Id}/Members?handler=Role",
            new() { ["userId"] = manager.Id.ToString(), ["role"] = "member" }, $"/Org/{org.Id}/Members");
        Assert.Equal(HttpStatusCode.Redirect, demote.StatusCode);
        var membership = await web.Service<IOrganizationRepository>().GetMembershipAsync(manager.Id, org.Id);
        Assert.Equal("manager", membership!.Role);
        Assert.Contains("last manager", await browser.GetStringAsync($"/Org/{org.Id}/Members"));
    }

    [Fact]
    public async Task People_with_an_account_are_invited_and_decide_themselves()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync();
        await web.AddMemberAsync(manager.Id, org.Id, "manager");
        var person = await web.CreateUserAsync(name: "Quiet Person");
        var browser = await web.SignedInBrowserAsync(manager.Email);

        var add = await WebFixture.PostFormAsync(browser, $"/Org/{org.Id}/Members?handler=Add",
            new() { ["email"] = person.Email, ["fullName"] = "", ["role"] = "member" }, $"/Org/{org.Id}/Members");
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Equal(MembershipStatus.Invited, (await web.Service<IOrganizationRepository>().GetMembershipAsync(person.Id, org.Id))!.Status);
        Assert.StartsWith("Invitation to", (await web.LastEmailToAsync(person.Email))!.Subject);

        // the manager sees the address, not the account behind it, and cannot open or change it
        var members = await browser.GetStringAsync($"/Org/{org.Id}/Members");
        Assert.Contains(person.Email, members);
        Assert.DoesNotContain("Quiet Person", members);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Org/{org.Id}/Member/{person.Id}")).StatusCode);

        // no access until the person accepts
        var app = await web.AppClientAsync(person.Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync($"/api/orgs/{org.Id}/books")).StatusCode);
        var own = await web.SignedInBrowserAsync(person.Email);
        Assert.Contains("You are invited to join", await own.GetStringAsync("/Account/Organizations"));
        var accept = await WebFixture.PostFormAsync(own, "/Account/Organizations?handler=Accept", new() { ["orgId"] = org.Id.ToString() }, "/Account/Organizations");
        Assert.Equal(HttpStatusCode.Redirect, accept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync($"/api/orgs/{org.Id}/books")).StatusCode);
        Assert.Contains("Quiet Person", await browser.GetStringAsync($"/Org/{org.Id}/Members"));
    }

    [Fact]
    public async Task Sign_in_failures_look_the_same_whatever_the_reason()
    {
        var user = await web.CreateUserAsync(password: "right password here");
        var client = web.CreateClient();
        async Task<string> LoginAsync(string email, string password)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password }, OnlineJson.Options);
            var error = await response.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options);
            return $"{(int)response.StatusCode} {error?.Code} {error?.Error}";
        }

        var unknown = await LoginAsync(WebFixture.Unique("nobody") + "@example.com", "whatever it is");
        var wrong = await LoginAsync(user.Email, "not the password");
        Assert.StartsWith("401", wrong);
        Assert.Equal(unknown, wrong);
        for (var i = 0; i < PracticalMusic.Web.Services.AccountService.MaxFailedLogins; i++) await LoginAsync(user.Email, "not the password");
        Assert.Equal(wrong, await LoginAsync(user.Email, "right password here")); // locked: still the same answer
    }

    [Fact]
    public async Task The_administrator_sets_up_an_organization_with_its_first_manager()
    {
        var admin = await web.CreateUserAsync(admin: true);
        var browser = await web.SignedInBrowserAsync(admin.Email);
        var name = WebFixture.Unique("Grace Church ");
        var managerEmail = WebFixture.Unique("pastor") + "@example.com";
        var response = await WebFixture.PostFormAsync(browser, "/Admin/Organizations", new()
        {
            ["Input.Name"] = name, ["Input.Kind"] = "church", ["Input.AllowJoinRequests"] = "true",
            ["Input.ManagerEmail"] = managerEmail, ["Input.ManagerName"] = "Pat Pastor"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var org = await web.Service<IOrganizationRepository>().FindByNameAsync(name);
        Assert.NotNull(org);
        var managers = await web.Service<IOrganizationRepository>().ListActiveManagersAsync(org!.Id);
        Assert.Equal(managerEmail, Assert.Single(managers).Email);
        Assert.Contains("invited", (await web.LastEmailToAsync(managerEmail))!.Subject);
    }
}
