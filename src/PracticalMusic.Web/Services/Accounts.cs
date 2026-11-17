using Microsoft.Extensions.Options;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;

namespace PracticalMusic.Web.Services;

/// <summary>One-time links sent by e-mail. Only hashes are stored; creating a link invalidates older links of the same purpose.</summary>
public sealed class UserTokenService(ITokenRepository tokens)
{
    public async Task<string> CreateAsync(long userId, string purpose, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        await tokens.InvalidateUserTokensAsync(userId, purpose, now);
        var token = Tokens.NewToken();
        await tokens.CreateUserTokenAsync(new UserTokenRow
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = Tokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + lifetime
        });
        return token;
    }

    /// <summary>The token's row when it exists, is unused, has not expired and has one of the purposes.</summary>
    public async Task<UserTokenRow?> FindValidAsync(string? token, params string[] purposes)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var row = await tokens.FindUserTokenAsync(Tokens.Hash(token.Trim()));
        if (row == null || row.UsedAt != null || row.ExpiresAt < DateTime.UtcNow || !purposes.Contains(row.Purpose)) return null;
        return row;
    }

    /// <summary>Uses the token up. False when a parallel request already used it - the caller must then stop.</summary>
    public Task<bool> ConsumeAsync(UserTokenRow row) => tokens.MarkUserTokenUsedAsync(row.Id, DateTime.UtcNow);
}

public sealed class RegisterInput
{
    public long OrgId { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string Password { get; set; } = "";
    public string? Message { get; set; }
}

public enum LoginStatus { Success, Invalid, Locked, NotVerified, Disabled }

public sealed record LoginResult(LoginStatus Status, UserRow? User = null, string? Message = null)
{
    public bool Succeeded => Status == LoginStatus.Success;
}

public sealed class AccountService(
    IUserRepository users, IOrganizationRepository orgs, ITokenRepository tokens, UserTokenService userTokens,
    PasswordHashing hashing, EmailService email, SiteUrls urls, IOptions<SiteOptions> site, Audit audit,
    UserCache cache, MembershipService memberships, Throttle throttle)
{
    public const int MaxFailedLogins = 10;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan VerifyLinkLifetime = TimeSpan.FromDays(2);
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The same answer for a wrong password, an unknown address and a temporarily locked account, so sign-in cannot be
    /// used to find out which addresses have accounts.
    /// </summary>
    public const string LoginFailedMessage =
        "The e-mail address or password is not correct. (After 10 failed attempts, sign-in pauses for 15 minutes - \"Forgot your password?\" gets you in right away.)";

    private string SiteName => site.Value.Name;

    // ------------------------------------------------------------------ sign-up

    /// <summary>
    /// Creates the account and a pending request to join the chosen organization. When the address is already
    /// registered nothing changes and the owner of the address gets a note instead; the caller shows the same
    /// "check your e-mail" page either way, so the form cannot be used to find out who has an account.
    /// </summary>
    public async Task RegisterAsync(RegisterInput input)
    {
        var org = await orgs.GetAsync(input.OrgId);
        if (org == null || !org.IsActive || !org.AllowJoinRequests)
            throw new UserFacingException("Choose an organization from the list.");
        var address = Text.NormalizeEmail(input.Email);
        if (!Text.IsValidEmail(address)) throw new UserFacingException("Enter a valid e-mail address.");
        var name = Text.Clean(input.FullName, 120) ?? throw new UserFacingException("Enter your name.");
        if (PasswordHashing.Validate(input.Password, address) is { } problem) throw new UserFacingException(problem);

        var existing = await users.FindByEmailAsync(address);
        if (existing != null)
        {
            hashing.BurnTime(input.Password); // same work as a real sign-up
            if (!existing.IsDisabled && throttle.Allow("exists:" + existing.Id, 2))
            {
                var reset = await userTokens.CreateAsync(existing.Id, TokenPurpose.ResetPassword, ResetLinkLifetime);
                await email.QueueAsync(existing.Email, existing.FullName, EmailTemplates.AccountExists(SiteName, existing.FullName,
                    urls.Absolute("/Account/Login"), urls.Absolute("/Account/ResetPassword?token=" + Uri.EscapeDataString(reset))));
            }
            await audit.WriteAsync("register.existing-address", $"{address} tried to sign up again", existing.Id, org.Id);
            return;
        }

        var now = DateTime.UtcNow;
        var verify = site.Value.RequireEmailVerification;
        var user = new UserRow
        {
            Email = address,
            PasswordHash = hashing.Hash(input.Password),
            FullName = name,
            Phone = Text.Clean(input.Phone, 40),
            Address = Text.Clean(input.Address, 500),
            EmailVerifiedAt = verify ? null : now,
            SecurityStamp = Tokens.NewSecurityStamp(),
            CreatedAt = now,
            UpdatedAt = now
        };
        user.Id = await users.CreateAsync(user);
        var message = Text.Clean(input.Message, 1000);
        await orgs.UpsertMembershipAsync(new MembershipRow
        {
            UserId = user.Id, OrgId = org.Id, Role = OrgRoles.Member, Status = MembershipStatus.Pending,
            RequestMessage = message, RequestedAt = now
        });
        await audit.WriteAsync("register", $"{name} <{address}> asked to join {org.Name}", user.Id, org.Id);

        if (verify) await SendVerificationAsync(user);
        else await memberships.NotifyManagersOfRequestAsync(org, user, message);
    }

    public async Task SendVerificationAsync(UserRow user)
    {
        var token = await userTokens.CreateAsync(user.Id, TokenPurpose.VerifyEmail, VerifyLinkLifetime);
        await email.QueueAsync(user.Email, user.FullName, EmailTemplates.VerifyEmail(SiteName, user.FullName,
            urls.Absolute("/Account/VerifyEmail?token=" + Uri.EscapeDataString(token))));
    }

    /// <summary>Sends a fresh confirmation link if the address belongs to an unconfirmed account (silently does nothing otherwise).</summary>
    public async Task ResendVerificationAsync(string emailAddress)
    {
        var user = await users.FindByEmailAsync(Text.NormalizeEmail(emailAddress));
        if (user == null || user.IsDisabled || user.IsEmailVerified || !throttle.Allow("verify:" + user.Id, 3)) return;
        await SendVerificationAsync(user);
    }

    /// <summary>Confirms the address. Returns the user, or null for an invalid/expired/used link.</summary>
    public async Task<UserRow?> VerifyEmailAsync(string? token)
    {
        var row = await userTokens.FindValidAsync(token, TokenPurpose.VerifyEmail);
        if (row == null || !await userTokens.ConsumeAsync(row)) return null;
        var user = await users.GetAsync(row.UserId);
        if (user == null) return null;
        if (!user.IsEmailVerified)
        {
            await users.SetEmailVerifiedAsync(user.Id, DateTime.UtcNow);
            cache.Invalidate(user.Id);
            await audit.WriteAsync("email.verified", user.Email, user.Id);
            // the managers hear about the join request only now that the address is known to be real
            await memberships.NotifyManagersOfPendingRequestsAsync(user);
        }
        return await users.GetAsync(user.Id);
    }

    // ------------------------------------------------------------------ sign-in

    public async Task<LoginResult> CheckPasswordAsync(string? emailAddress, string? password)
    {
        password ??= "";
        var user = await users.FindByEmailAsync(Text.NormalizeEmail(emailAddress));
        if (user == null)
        {
            hashing.BurnTime(password);
            return new LoginResult(LoginStatus.Invalid, Message: LoginFailedMessage);
        }
        var now = DateTime.UtcNow;
        if (user.LockedUntil > now)
        {
            hashing.BurnTime(password);
            return new LoginResult(LoginStatus.Locked, Message: LoginFailedMessage);
        }
        var (ok, rehash) = hashing.Verify(user.PasswordHash, password);
        if (!ok)
        {
            var locked = await users.RecordLoginFailureAsync(user.Id, MaxFailedLogins, now + LockoutTime);
            cache.Invalidate(user.Id);
            await audit.WriteAsync(locked ? "login.locked" : "login.failed", user.Email, user.Id);
            return new LoginResult(locked ? LoginStatus.Locked : LoginStatus.Invalid, Message: LoginFailedMessage);
        }
        if (user.IsDisabled) return new LoginResult(LoginStatus.Disabled, user, "This account is disabled. Contact the website administrator.");
        if (site.Value.RequireEmailVerification && !user.IsEmailVerified) return new LoginResult(LoginStatus.NotVerified, user);

        if (rehash) await users.SetPasswordAsync(user.Id, hashing.Hash(password), user.SecurityStamp, now);
        await users.RecordLoginSuccessAsync(user.Id, now);
        cache.Invalidate(user.Id);
        await audit.WriteAsync("login", null, user.Id);
        return new LoginResult(LoginStatus.Success, await users.GetAsync(user.Id) ?? user);
    }

    /// <summary>Creates an app token (desktop/Android sign-in).</summary>
    public async Task<LoginResponse> IssueApiTokenAsync(UserRow user, string? deviceName, string? platform)
    {
        var now = DateTime.UtcNow;
        var token = Tokens.NewToken();
        var expires = now.AddDays(Math.Clamp(site.Value.ApiTokenDays, 1, 3650));
        var device = Text.Clean(deviceName, 120) ?? "Practical Music app";
        await tokens.CreateApiTokenAsync(new ApiTokenRow
        {
            UserId = user.Id,
            TokenHash = Tokens.Hash(token),
            DeviceName = device,
            Platform = Text.Clean(platform, 40),
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = expires
        });
        await audit.WriteAsync("app.signin", $"{device} ({platform})", user.Id);
        return new LoginResponse { Token = token, ExpiresAt = new DateTimeOffset(expires, TimeSpan.Zero), User = await BuildUserInfoAsync(user) };
    }

    public async Task<UserInfo> BuildUserInfoAsync(UserRow user)
    {
        List<OrgMembershipInfo> list;
        if (user.IsSiteAdmin)
        {
            list = (await orgs.ListAsync(includeInactive: false))
                .Select(o => new OrgMembershipInfo { Id = o.Id, Name = o.Name, Kind = o.Kind, Role = OrgRoles.Manager, CanEdit = true }).ToList();
        }
        else
        {
            list = (await orgs.ListForUserAsync(user.Id))
                .Where(m => m.Status == MembershipStatus.Active && m.IsActive)
                .Select(m => new OrgMembershipInfo { Id = m.OrgId, Name = m.Name, Kind = m.Kind, Role = m.Role, CanEdit = m.Role == OrgRoles.Manager })
                .ToList();
        }
        return new UserInfo { Id = user.Id, Email = user.Email, FullName = user.FullName, IsSiteAdmin = user.IsSiteAdmin, Organizations = list };
    }

    // ------------------------------------------------------------------ passwords

    /// <summary>Sends a reset link when the address belongs to an active account. Always looks the same to the caller.</summary>
    public async Task ForgotPasswordAsync(string? emailAddress)
    {
        var user = await users.FindByEmailAsync(Text.NormalizeEmail(emailAddress));
        if (user == null || user.IsDisabled) return;
        if (!throttle.Allow("reset:" + user.Id, 3)) return; // at most 3 reset e-mails per hour per account
        var token = await userTokens.CreateAsync(user.Id, TokenPurpose.ResetPassword, ResetLinkLifetime);
        await email.QueueAsync(user.Email, user.FullName, EmailTemplates.PasswordReset(SiteName, user.FullName,
            urls.Absolute("/Account/ResetPassword?token=" + Uri.EscapeDataString(token)), (int)ResetLinkLifetime.TotalMinutes));
        await audit.WriteAsync("password.reset-requested", null, user.Id);
    }

    /// <summary>For the reset page: is the link still good, and is it an invitation (new account) or a reset?</summary>
    public async Task<(UserRow User, bool IsInvite)?> PeekResetTokenAsync(string? token)
    {
        var row = await userTokens.FindValidAsync(token, TokenPurpose.ResetPassword, TokenPurpose.Invite);
        if (row == null) return null;
        var user = await users.GetAsync(row.UserId);
        return user == null || user.IsDisabled ? null : (user, row.Purpose == TokenPurpose.Invite);
    }

    /// <summary>Sets a new password from an e-mailed link (reset or invitation). Signs out every other session and app.</summary>
    public async Task<UserRow> ResetPasswordAsync(string? token, string newPassword)
    {
        const string usedUp = "This link has expired or was already used. Request a new one with \"Forgot password\".";
        var row = await userTokens.FindValidAsync(token, TokenPurpose.ResetPassword, TokenPurpose.Invite) ?? throw new UserFacingException(usedUp);
        var user = await users.GetAsync(row.UserId) ?? throw new UserFacingException("This account no longer exists.");
        if (user.IsDisabled) throw new UserFacingException("This account is disabled.");
        if (PasswordHashing.Validate(newPassword, user.Email) is { } problem) throw new UserFacingException(problem);
        if (!await userTokens.ConsumeAsync(row)) throw new UserFacingException(usedUp);

        var now = DateTime.UtcNow;
        var wasVerified = user.IsEmailVerified;
        await tokens.InvalidateUserTokensAsync(user.Id, TokenPurpose.ResetPassword, now);
        await tokens.InvalidateUserTokensAsync(user.Id, TokenPurpose.Invite, now);
        await users.SetPasswordAsync(user.Id, hashing.Hash(newPassword), Tokens.NewSecurityStamp(), now);
        await users.SetEmailVerifiedAsync(user.Id, now); // the link proved the address
        var isReset = row.Purpose == TokenPurpose.ResetPassword;
        if (isReset)
        {
            await cache.RevokeAllAppTokensAsync(tokens, user.Id);
            await email.QueueAsync(user.Email, user.FullName, EmailTemplates.PasswordChanged(SiteName, user.FullName, urls.Absolute("/Account/ForgotPassword")));
        }
        cache.Invalidate(user.Id);
        await audit.WriteAsync(isReset ? "password.reset" : "invite.accepted", null, user.Id);
        var updated = await users.GetAsync(user.Id) ?? user;
        if (!wasVerified) await memberships.NotifyManagersOfPendingRequestsAsync(updated);
        return updated;
    }

    public async Task<UserRow> ChangePasswordAsync(long userId, string currentPassword, string newPassword, bool signOutApps)
    {
        var user = await users.GetAsync(userId) ?? throw UserFacingException.NotFound();
        if (!hashing.Verify(user.PasswordHash, currentPassword).Valid) throw new UserFacingException("The current password is not correct.");
        if (PasswordHashing.Validate(newPassword, user.Email) is { } problem) throw new UserFacingException(problem);
        await users.SetPasswordAsync(user.Id, hashing.Hash(newPassword), Tokens.NewSecurityStamp(), DateTime.UtcNow);
        if (signOutApps) await cache.RevokeAllAppTokensAsync(tokens, user.Id);
        cache.Invalidate(user.Id);
        await email.QueueAsync(user.Email, user.FullName, EmailTemplates.PasswordChanged(SiteName, user.FullName, urls.Absolute("/Account/ForgotPassword")));
        await audit.WriteAsync("password.changed", signOutApps ? "apps signed out" : null, user.Id);
        return await users.GetAsync(user.Id) ?? user;
    }

    public async Task UpdateProfileAsync(long userId, string? fullName, string? phone, string? address, long actorId, long? orgId = null)
    {
        var name = Text.Clean(fullName, 120) ?? throw new UserFacingException("Enter a name.");
        await users.UpdateProfileAsync(userId, name, Text.Clean(phone, 40), Text.Clean(address, 500), DateTime.UtcNow);
        cache.Invalidate(userId);
        await audit.WriteAsync("profile.updated", actorId == userId ? null : $"user {userId}", actorId, orgId);
    }
}

/// <summary>Who belongs to which organization: join requests, approvals, invitations, roles.</summary>
public sealed class MembershipService(
    IOrganizationRepository orgs, IUserRepository users, ITokenRepository tokens, UserTokenService userTokens, EmailService email,
    SiteUrls urls, IOptions<SiteOptions> site, Audit audit, UserCache cache, Throttle throttle)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    private string SiteName => site.Value.Name;

    public async Task<string> RequestJoinAsync(UserRow user, long orgId, string? message)
    {
        var org = await orgs.GetAsync(orgId) ?? throw UserFacingException.NotFound("Organization not found.");
        var existing = await orgs.GetMembershipAsync(user.Id, orgId);
        if (existing?.Status == MembershipStatus.Invited) return await AcceptInvitationAsync(user, orgId); // they were invited already
        if (!org.IsActive || !org.AllowJoinRequests) throw new UserFacingException($"{org.Name} does not accept requests at the moment.");
        if (existing?.Status == MembershipStatus.Active) return $"You already belong to {org.Name}.";
        if (existing?.Status == MembershipStatus.Pending) return $"Your request to join {org.Name} is already waiting for a manager.";
        var text = Text.Clean(message, 1000);
        await orgs.UpsertMembershipAsync(new MembershipRow
        {
            UserId = user.Id, OrgId = orgId, Role = OrgRoles.Member, Status = MembershipStatus.Pending,
            RequestMessage = text, RequestedAt = DateTime.UtcNow
        });
        cache.Invalidate(user.Id);
        await audit.WriteAsync("join.requested", org.Name, user.Id, orgId);
        if (user.IsEmailVerified || !site.Value.RequireEmailVerification) await NotifyManagersOfRequestAsync(org, user, text);
        return $"Your request was sent to the managers of {org.Name}. You will receive an e-mail when it is approved.";
    }

    /// <summary>E-mails every manager of the organization (or the website administrators when it has none) - at most 3 times an hour per person and organization.</summary>
    public async Task NotifyManagersOfRequestAsync(OrgRow org, UserRow applicant, string? message)
    {
        if (!throttle.Allow($"join:{applicant.Id}:{org.Id}", 3)) return;
        var recipients = await orgs.ListActiveManagersAsync(org.Id);
        if (recipients.Count == 0) recipients = await users.ListSiteAdminsAsync();
        var link = urls.Absolute($"/Org/{org.Id}/Members");
        foreach (var manager in recipients)
            await email.QueueAsync(manager.Email, manager.FullName, EmailTemplates.JoinRequest(SiteName, manager.FullName, applicant.FullName,
                applicant.Email, applicant.Phone, applicant.Address, org.Name, message, link));
    }

    public async Task NotifyManagersOfPendingRequestsAsync(UserRow user)
    {
        foreach (var m in (await orgs.ListForUserAsync(user.Id)).Where(m => m.Status == MembershipStatus.Pending && m.IsActive))
        {
            var org = await orgs.GetAsync(m.OrgId);
            var membership = await orgs.GetMembershipAsync(user.Id, m.OrgId);
            if (org != null) await NotifyManagersOfRequestAsync(org, user, membership?.RequestMessage);
        }
    }

    /// <summary>Approves a waiting (or earlier declined) request to join.</summary>
    public async Task ApproveAsync(long orgId, long userId, string role, UserRow actor)
    {
        var org = await orgs.GetAsync(orgId) ?? throw UserFacingException.NotFound("Organization not found.");
        var m = await orgs.GetMembershipAsync(userId, orgId) ?? throw UserFacingException.NotFound("That request no longer exists.");
        if (m.Status is not (MembershipStatus.Pending or MembershipStatus.Rejected))
            throw new UserFacingException("Only requests to join can be approved. To change someone's role, use the role list.");
        var user = await users.GetAsync(userId) ?? throw UserFacingException.NotFound("That user no longer exists.");
        m.Status = MembershipStatus.Active;
        m.Role = OrgRoles.IsValid(role) ? role : OrgRoles.Member;
        m.DecidedAt = DateTime.UtcNow;
        m.DecidedBy = actor.Id;
        await orgs.UpsertMembershipAsync(m);
        cache.Invalidate(userId);
        await audit.WriteAsync("join.approved", $"{user.FullName} <{user.Email}> as {m.Role}", actor.Id, orgId);
        await email.QueueAsync(user.Email, user.FullName, EmailTemplates.AccessGranted(SiteName, user.FullName, org.Name, urls.Absolute($"/App?org={orgId}")));
    }

    public async Task RejectAsync(long orgId, long userId, UserRow actor, bool notify)
    {
        var org = await orgs.GetAsync(orgId) ?? throw UserFacingException.NotFound("Organization not found.");
        var m = await orgs.GetMembershipAsync(userId, orgId) ?? throw UserFacingException.NotFound("That request no longer exists.");
        if (m.Status != MembershipStatus.Pending) throw new UserFacingException("Only waiting requests can be declined. To take access away, remove the member.");
        var user = await users.GetAsync(userId);
        m.Status = MembershipStatus.Rejected;
        m.DecidedAt = DateTime.UtcNow;
        m.DecidedBy = actor.Id;
        await orgs.UpsertMembershipAsync(m);
        cache.Invalidate(userId);
        await audit.WriteAsync("join.declined", user?.Email, actor.Id, orgId);
        if (notify && user != null) await email.QueueAsync(user.Email, user.FullName, EmailTemplates.AccessDeclined(SiteName, user.FullName, org.Name));
    }

    /// <summary>
    /// Gives an e-mail address access. Unknown addresses (and accounts whose address was never confirmed) get an
    /// account/invitation to choose a password. People with a confirmed account are invited and must accept on their
    /// account page - managers cannot attach someone else's account without consent. The website administrator's
    /// additions take effect right away. The answer never reveals whether the address had an account.
    /// </summary>
    public async Task<string> AddMemberAsync(long orgId, string? emailAddress, string? fullName, string role, UserRow actor)
    {
        var org = await orgs.GetAsync(orgId) ?? throw UserFacingException.NotFound("Organization not found.");
        var address = Text.NormalizeEmail(emailAddress);
        if (!Text.IsValidEmail(address)) throw new UserFacingException("Enter a valid e-mail address.");
        role = OrgRoles.IsValid(role) ? role : OrgRoles.Member;
        var now = DateTime.UtcNow;
        var user = await users.FindByEmailAsync(address);
        var done = $"{address} was invited. They receive an e-mail; their access starts once they accept.";

        if (user == null)
        {
            var name = Text.Clean(fullName, 120) ?? throw new UserFacingException("Enter the person's name.");
            user = new UserRow { Email = address, FullName = name, SecurityStamp = Tokens.NewSecurityStamp(), CreatedAt = now, UpdatedAt = now };
            user.Id = await users.CreateAsync(user);
            await orgs.UpsertMembershipAsync(new MembershipRow
            {
                UserId = user.Id, OrgId = orgId, Role = role, Status = MembershipStatus.Active, RequestedAt = now, DecidedAt = now, DecidedBy = actor.Id
            });
            await SendInvitationAsync(user, org, actor);
            await audit.WriteAsync("member.invited", $"{name} <{address}> as {role}", actor.Id, orgId);
            return done;
        }

        var existing = await orgs.GetMembershipAsync(user.Id, orgId);
        if (existing?.Status == MembershipStatus.Active) return $"{address} already belongs to {org.Name}.";

        if (!user.IsEmailVerified || !user.HasPassword)
        {
            // Nobody has proven they own this address yet (e.g. someone signed up with it and never confirmed):
            // remove that password so only the invitation link - sent to the real owner - can set one.
            if (user.HasPassword)
            {
                await users.SetPasswordAsync(user.Id, null, Tokens.NewSecurityStamp(), now);
                await cache.RevokeAllAppTokensAsync(tokens, user.Id);
            }
            await orgs.UpsertMembershipAsync(new MembershipRow
            {
                UserId = user.Id, OrgId = orgId, Role = role, Status = MembershipStatus.Active, RequestMessage = existing?.RequestMessage,
                RequestedAt = now, DecidedAt = now, DecidedBy = actor.Id
            });
            cache.Invalidate(user.Id);
            await SendInvitationAsync(user, org, actor);
            await audit.WriteAsync("member.invited", $"{address} as {role} (unconfirmed account reset)", actor.Id, orgId);
            return done;
        }

        var immediate = actor.IsSiteAdmin;
        await orgs.UpsertMembershipAsync(new MembershipRow
        {
            UserId = user.Id, OrgId = orgId, Role = role, Status = immediate ? MembershipStatus.Active : MembershipStatus.Invited,
            RequestMessage = existing?.RequestMessage, RequestedAt = now, DecidedAt = now, DecidedBy = actor.Id
        });
        cache.Invalidate(user.Id);
        if (throttle.Allow("invite:" + user.Id, 5))
            await email.QueueAsync(user.Email, user.FullName, immediate
                ? EmailTemplates.AddedToOrganization(SiteName, user.FullName, org.Name, actor.FullName, urls.Absolute($"/App?org={orgId}"))
                : EmailTemplates.InvitationToJoin(SiteName, user.FullName, org.Name, actor.FullName, urls.Absolute("/Account/Organizations")));
        await audit.WriteAsync(immediate ? "member.added" : "member.invited", $"{address} as {role}", actor.Id, orgId);
        return immediate ? $"{address} now has access to {org.Name}." : done;
    }

    public async Task SendInvitationAsync(UserRow user, OrgRow org, UserRow actor)
    {
        if (!throttle.Allow("invite:" + user.Id, 5)) throw new UserFacingException("Several invitations were sent to this address in the last hour. Try again later.");
        var token = await userTokens.CreateAsync(user.Id, TokenPurpose.Invite, InviteLifetime);
        await email.QueueAsync(user.Email, user.FullName, EmailTemplates.Invitation(SiteName, user.FullName, org.Name, actor.FullName,
            urls.Absolute("/Account/ResetPassword?token=" + Uri.EscapeDataString(token)), (int)InviteLifetime.TotalDays));
    }

    /// <summary>The invited person accepts (from their account page).</summary>
    public async Task<string> AcceptInvitationAsync(UserRow user, long orgId)
    {
        var org = await orgs.GetAsync(orgId) ?? throw UserFacingException.NotFound("Organization not found.");
        var m = await orgs.GetMembershipAsync(user.Id, orgId);
        if (m?.Status != MembershipStatus.Invited) throw new UserFacingException("There is no invitation to accept.");
        m.Status = MembershipStatus.Active;
        m.DecidedAt = DateTime.UtcNow;
        await orgs.UpsertMembershipAsync(m);
        cache.Invalidate(user.Id);
        await audit.WriteAsync("invite.accepted", org.Name, user.Id, orgId);
        return $"You now have access to {org.Name}.";
    }

    public async Task RemoveAsync(long orgId, long userId, UserRow actor)
    {
        var m = await orgs.GetMembershipAsync(userId, orgId) ?? throw UserFacingException.NotFound("That person is not a member.");
        await EnsureNotLastManagerAsync(orgId, m, actor, "remove");
        await orgs.DeleteMembershipAsync(userId, orgId);
        cache.Invalidate(userId);
        var user = await users.GetAsync(userId);
        await audit.WriteAsync(actor.Id == userId ? "member.left" : "member.removed", user?.Email, actor.Id, orgId);
    }

    public async Task SetRoleAsync(long orgId, long userId, string role, UserRow actor)
    {
        if (!OrgRoles.IsValid(role)) throw new UserFacingException("Unknown role.");
        var m = await orgs.GetMembershipAsync(userId, orgId) ?? throw UserFacingException.NotFound("That person is not a member.");
        if (m.Status is not (MembershipStatus.Active or MembershipStatus.Invited)) throw new UserFacingException("Approve the request first.");
        if (m.Role == role) return;
        if (role == OrgRoles.Member) await EnsureNotLastManagerAsync(orgId, m, actor, "change");
        m.Role = role;
        await orgs.UpsertMembershipAsync(m);
        cache.Invalidate(userId);
        var user = await users.GetAsync(userId);
        await audit.WriteAsync("member.role", $"{user?.Email} is now {role}", actor.Id, orgId);
    }

    /// <summary>Every organization keeps at least one manager; only the website administrator may remove the last one.</summary>
    private async Task EnsureNotLastManagerAsync(long orgId, MembershipRow m, UserRow actor, string verb)
    {
        if (m.Role != OrgRoles.Manager || m.Status != MembershipStatus.Active || actor.IsSiteAdmin) return;
        if (await orgs.CountActiveManagersAsync(orgId) <= 1)
            throw new UserFacingException($"You cannot {verb} the last manager of the organization. Make someone else a manager first.");
    }
}
