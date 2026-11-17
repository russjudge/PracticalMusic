using System.Net.WebSockets;
using System.Text.Json;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;
using Xunit;

namespace PracticalMusic.Web.Tests;

[Collection("web")]
public sealed class SyncHubTests(WebFixture web)
{
    private sealed class Peer(WebSocket socket) : IAsyncDisposable
    {
        public WebSocket Socket { get; } = socket;

        public async Task SendAsync(SyncWireMessage message) =>
            await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, OnlineJson.Options), WebSocketMessageType.Text, true, CancellationToken.None);

        /// <summary>Next message of the given type (others are skipped).</summary>
        public async Task<SyncWireMessage> ExpectAsync(string type, Func<SyncWireMessage, bool>? where = null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await Socket.ReceiveAsync(buffer, cts.Token);
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("socket closed");
                var msg = JsonSerializer.Deserialize<SyncWireMessage>(ms.ToArray(), OnlineJson.Options)!;
                if (msg.Type == type && (where == null || where(msg))) return msg;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { /* gone */ }
            Socket.Dispose();
        }
    }

    private async Task<Peer> ConnectAsync(string email)
    {
        var app = await web.AppClientAsync(email);
        var token = app.DefaultRequestHeaders.Authorization!.Parameter!;
        var client = web.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        var socket = await client.ConnectAsync(new Uri(web.Server.BaseAddress, "ws/sync"), CancellationToken.None);
        return new Peer(socket);
    }

    [Fact]
    public async Task A_leader_in_one_place_is_followed_in_another()
    {
        var org = await web.CreateOrgAsync();
        var leaderUser = await web.CreateUserAsync(name: "Lee Leader");
        var followerUser = await web.CreateUserAsync(name: "Fran Follower");
        await web.AddMemberAsync(leaderUser.Id, org.Id, "member");
        await web.AddMemberAsync(followerUser.Id, org.Id, "member");

        await using var leader = await ConnectAsync(leaderUser.Email);
        await leader.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "Lee's laptop", Platform = "Windows", BookTitle = "Sunday", BookUid = "abc" });
        var welcome = await leader.ExpectAsync("welcome");
        Assert.False(string.IsNullOrEmpty(welcome.ConnectionId));
        Assert.True(welcome.ServerTimeMs > 0);

        await using var follower = await ConnectAsync(followerUser.Email);
        await follower.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "Fran's iPad", Platform = "Browser" });
        await follower.ExpectAsync("welcome");
        var peers = await follower.ExpectAsync("peers", m => m.Peers!.Count == 2);
        var lee = peers.Peers!.Single(p => p.Id == welcome.ConnectionId);
        Assert.Equal("Lee's laptop", lee.Name);
        Assert.Equal("Lee Leader", lee.UserName);
        Assert.Equal("Sunday", lee.BookTitle);

        // clock: ping → pong carries the server time
        await follower.SendAsync(new SyncWireMessage { Type = "ping", T0 = 123.5 });
        var pong = await follower.ExpectAsync("pong");
        Assert.Equal(123.5, pong.T0);
        Assert.True(pong.T1 > 0);

        // lead, follow, publish: the follower receives the state (times stay on the server clock)
        await leader.SendAsync(new SyncWireMessage { Type = "lead" });
        await follower.ExpectAsync("peers", m => m.Peers!.Any(p => p.Id == welcome.ConnectionId && p.Leading));
        await follower.SendAsync(new SyncWireMessage { Type = "follow", LeaderId = welcome.ConnectionId });
        var state = new SyncState { BookId = "abc", SongId = "s1", SongTitle = "Amazing Grace", IsPlaying = true, PositionMs = 4200, AtLeaderTimeMs = pong.T1!.Value + 700 };
        await leader.SendAsync(new SyncWireMessage { Type = "state", State = state });
        var relayed = await follower.ExpectAsync("state");
        Assert.Equal(welcome.ConnectionId, relayed.LeaderId);
        Assert.Equal("Amazing Grace", relayed.State!.SongTitle);
        Assert.Equal(4200, relayed.State.PositionMs);
        Assert.Equal(state.AtLeaderTimeMs, relayed.State.AtLeaderTimeMs);

        // the leader stops → the follower is told
        await leader.SendAsync(new SyncWireMessage { Type = "unlead" });
        var gone = await follower.ExpectAsync("leaderGone");
        Assert.Equal(welcome.ConnectionId, gone.LeaderId);
    }

    [Fact]
    public async Task Late_followers_get_the_last_state_and_leaving_ends_the_session()
    {
        var org = await web.CreateOrgAsync();
        var a = await web.CreateUserAsync();
        var b = await web.CreateUserAsync();
        await web.AddMemberAsync(a.Id, org.Id, "manager");
        await web.AddMemberAsync(b.Id, org.Id, "member");

        var leader = await ConnectAsync(a.Email);
        await leader.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "A" });
        var id = (await leader.ExpectAsync("welcome")).ConnectionId;
        await leader.SendAsync(new SyncWireMessage { Type = "lead" });
        await leader.SendAsync(new SyncWireMessage { Type = "state", State = new SyncState { SongTitle = "Late", IsPlaying = false, PositionMs = 9000 } });

        await using var follower = await ConnectAsync(b.Email);
        await follower.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "B" });
        await follower.ExpectAsync("peers", m => m.Peers!.Any(p => p.Id == id && p.Leading));
        await follower.SendAsync(new SyncWireMessage { Type = "follow", LeaderId = id });
        Assert.Equal("Late", (await follower.ExpectAsync("state")).State!.SongTitle);

        await leader.DisposeAsync(); // the leader's device goes away
        Assert.Equal(id, (await follower.ExpectAsync("leaderGone")).LeaderId);
    }

    [Fact]
    public async Task Only_members_join_an_organizations_session()
    {
        var org = await web.CreateOrgAsync();
        var outsider = await web.CreateUserAsync();
        await using var peer = await ConnectAsync(outsider.Email);
        await peer.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "Intruder" });
        var error = await peer.ExpectAsync("error");
        Assert.Contains("access", error.Message);
        await peer.SendAsync(new SyncWireMessage { Type = "lead" });
        Assert.Contains("hello", (await peer.ExpectAsync("error")).Message);
    }

    [Fact]
    public async Task People_who_lose_access_are_disconnected()
    {
        var hub = web.Service<PracticalMusic.Web.Services.SyncHub>();
        var org = await web.CreateOrgAsync();
        var user = await web.CreateUserAsync();
        await web.AddMemberAsync(user.Id, org.Id, "member");
        await using var peer = await ConnectAsync(user.Email);
        await peer.SendAsync(new SyncWireMessage { Type = "hello", OrgId = org.Id, Name = "Soon gone" });
        await peer.ExpectAsync("welcome");

        // removed from the organization while connected: the next check ends the session
        await web.Service<PracticalMusic.Web.Data.IOrganizationRepository>().DeleteMembershipAsync(user.Id, org.Id);
        web.Service<PracticalMusic.Web.Security.UserCache>().Invalidate(user.Id);
        var previous = hub.AccessRecheck;
        hub.AccessRecheck = TimeSpan.Zero;
        try
        {
            await Task.Delay(20);
            await peer.SendAsync(new SyncWireMessage { Type = "ping", T0 = 1 });
            Assert.Contains("no longer", (await peer.ExpectAsync("error")).Message);
        }
        finally
        {
            hub.AccessRecheck = previous;
        }
    }

    [Fact]
    public async Task One_person_cannot_open_unlimited_connections()
    {
        var user = await web.CreateUserAsync();
        var open = new List<Peer>();
        try
        {
            for (var i = 0; i < PracticalMusic.Web.Services.SyncHub.MaxConnectionsPerUser; i++) open.Add(await ConnectAsync(user.Email));
            // all of them work
            await open[^1].SendAsync(new SyncWireMessage { Type = "ping", T0 = 5 });
            Assert.Equal(5, (await open[^1].ExpectAsync("pong")).T0);

            await using var extra = await ConnectAsync(user.Email);
            var buffer = new byte[1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await extra.Socket.ReceiveAsync(buffer, cts.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, extra.Socket.CloseStatus);
        }
        finally
        {
            foreach (var p in open) await p.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_socket_needs_a_sign_in()
    {
        var client = web.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri(web.Server.BaseAddress, "ws/sync"), CancellationToken.None));
    }
}
