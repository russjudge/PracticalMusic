using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;

namespace PracticalMusic.Web.Services;

/// <summary>
/// Synchronized playback through the website (see <see cref="SyncWireMessage"/> for the protocol). Browsers
/// cannot take part in the apps' LAN protocol (UDP broadcast, raw TCP), so everyone connected to an
/// organization - browsers and apps - meets here: anyone may lead, the others follow, and this hub relays
/// the leader's playback state. Times travel on this server's monotonic clock; clients measure their
/// offset to it with ping/pong. All state is in memory; nothing about a session is stored.
///
/// Abuse limits: connections per person, per organization and in total; messages per second (checked before
/// parsing); peer lists are debounced and serialized once; every connection has a bounded send queue (a client
/// that does not read is disconnected instead of slowing the others); access is re-checked every 30 seconds.
/// </summary>
public sealed class SyncHub(ILogger<SyncHub> log)
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxMessagesPerSecond = 40;
    public const int MaxConnectionsPerUser = 8;
    public const int MaxConnectionsPerOrg = 300;
    public const int MaxConnectionsTotal = 3000;
    private const int SendQueueLength = 256;
    /// <summary>How often a connection's access is checked again (settable for tests).</summary>
    public TimeSpan AccessRecheck { get; set; } = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerBroadcastDelay = TimeSpan.FromMilliseconds(150);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<long, byte> _peerBroadcastPending = new();

    public sealed class Connection(string id, WebSocket socket, long userId, string userName)
    {
        public string Id { get; } = id;
        public WebSocket Socket { get; } = socket;
        public long UserId { get; } = userId;
        public string UserName { get; } = userName;
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueLength)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        public long? OrgId { get; set; }
        public string Name { get; set; } = "";
        public string Platform { get; set; } = "";
        public long? ServerBookId { get; set; }
        public string? BookUid { get; set; }
        public string? BookTitle { get; set; }
        public bool Leading { get; set; }
        public string? FollowingId { get; set; }
        /// <summary>The last state this leader published (sent to anyone who starts following).</summary>
        public SyncState? LastState { get; set; }
        public long WindowStart { get; set; }
        public int WindowCount { get; set; }
        public DateTime LastAccessCheck { get; set; } = DateTime.UtcNow;
    }

    public int ConnectionCount => _connections.Count;

    /// <param name="canJoinOrg">Checks (freshly, every time) whether the user may be in an organization - also whether the sign-in is still valid.</param>
    public async Task RunAsync(WebSocket socket, long userId, string userName, Func<long, Task<bool>> canJoinOrg, CancellationToken ct)
    {
        if (_connections.Count >= MaxConnectionsTotal || _connections.Values.Count(c => c.UserId == userId) >= MaxConnectionsPerUser)
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "too many connections", closing.Token); } catch { /* gone */ }
            return;
        }
        var connection = new Connection(Guid.NewGuid().ToString("N")[..16], socket, userId, userName);
        _connections[connection.Id] = connection;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendLoopAsync(connection, stop.Token);
        try
        {
            await ReceiveLoopAsync(connection, canJoinOrg, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sync connection {Id} failed", connection.Id);
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            LeaveOrg(connection);
            // what is still queued (such as an error explaining why the connection ends) goes out first
            connection.Outbox.Writer.TryComplete();
            if (await Task.WhenAny(sender, Task.Delay(TimeSpan.FromSeconds(12))) != sender) socket.Abort();
            stop.Cancel();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
                catch { /* gone */ }
            }
        }
    }

    private static async Task SendLoopAsync(Connection c, CancellationToken ct)
    {
        try
        {
            while (await c.Outbox.Reader.WaitToReadAsync(ct))
            {
                while (c.Outbox.Reader.TryRead(out var bytes))
                {
                    if (c.Socket.State != WebSocketState.Open) return;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await c.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
                }
            }
        }
        catch
        {
            c.Socket.Abort(); // a client that cannot keep up is dropped; it reconnects by itself
        }
    }

    private async Task ReceiveLoopAsync(Connection c, Func<long, Task<bool>> canJoinOrg, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && c.Socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(90)); // clients ping every few seconds
                result = await c.Socket.ReceiveAsync(buffer, idle.Token);
            }
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await c.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage) continue;
            if (!WithinRate(c))
            {
                message.SetLength(0); // over the limit: dropped without even parsing it
                continue;
            }

            SyncWireMessage? msg = null;
            try { msg = JsonSerializer.Deserialize<SyncWireMessage>(message.GetBuffer().AsSpan(0, (int)message.Length), OnlineJson.Options); }
            catch (JsonException) { /* ignore garbage */ }
            message.SetLength(0);
            if (msg == null) continue;

            if (c.OrgId is { } orgId && DateTime.UtcNow - c.LastAccessCheck > AccessRecheck)
            {
                c.LastAccessCheck = DateTime.UtcNow;
                if (!await canJoinOrg(orgId))
                {
                    Send(c, new SyncWireMessage { Type = "error", Message = "You no longer have access to this organization." });
                    return; // the message is delivered before the connection closes
                }
            }
            await HandleAsync(c, msg, canJoinOrg);
        }
    }

    private static bool WithinRate(Connection c)
    {
        var second = Environment.TickCount64 / 1000;
        if (second != c.WindowStart)
        {
            c.WindowStart = second;
            c.WindowCount = 0;
        }
        return ++c.WindowCount <= MaxMessagesPerSecond;
    }

    private static string Clean(string? s, int max, string fallback)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return fallback;
        return t.Length > max ? t[..max] : t;
    }

    private async Task HandleAsync(Connection c, SyncWireMessage msg, Func<long, Task<bool>> canJoinOrg)
    {
        var now = MonoClock.NowMs;
        if (c.OrgId == null && msg.Type is not ("hello" or "ping"))
        {
            Send(c, new SyncWireMessage { Type = "error", Message = "Send hello first." });
            return;
        }
        switch (msg.Type)
        {
            case "hello":
            {
                if (msg.OrgId is not { } orgId || !await canJoinOrg(orgId))
                {
                    Send(c, new SyncWireMessage { Type = "error", Message = "You do not have access to that organization." });
                    return;
                }
                if (c.OrgId != orgId && _connections.Values.Count(x => x.OrgId == orgId) >= MaxConnectionsPerOrg)
                {
                    Send(c, new SyncWireMessage { Type = "error", Message = "Too many devices are connected to this organization right now." });
                    return;
                }
                if (c.OrgId != orgId) LeaveOrg(c);
                c.OrgId = orgId;
                c.LastAccessCheck = DateTime.UtcNow;
                c.Name = Clean(msg.Name, 60, c.UserName);
                c.Platform = Clean(msg.Platform, 30, "Browser");
                c.ServerBookId = msg.ServerBookId;
                c.BookUid = msg.BookUid is { Length: <= 64 } uid ? uid : null;
                c.BookTitle = msg.BookTitle == null ? null : Clean(msg.BookTitle, 200, "");
                Send(c, new SyncWireMessage { Type = "welcome", ConnectionId = c.Id, OrgId = orgId, ServerTimeMs = now });
                SchedulePeers(orgId);
                break;
            }
            case "book":
                c.ServerBookId = msg.ServerBookId;
                c.BookUid = msg.BookUid is { Length: <= 64 } u ? u : null;
                c.BookTitle = msg.BookTitle == null ? null : Clean(msg.BookTitle, 200, "");
                SchedulePeers(c.OrgId!.Value);
                break;
            case "ping":
                Send(c, new SyncWireMessage { Type = "pong", T0 = msg.T0, T1 = now, ServerTimeMs = now });
                break;
            case "lead":
                if (c.FollowingId != null) c.FollowingId = null;
                if (!c.Leading)
                {
                    c.Leading = true;
                    c.LastState = null;
                    SchedulePeers(c.OrgId!.Value);
                }
                break;
            case "unlead":
                if (c.Leading)
                {
                    StopLeading(c);
                    SchedulePeers(c.OrgId!.Value);
                }
                break;
            case "follow":
            {
                var leader = msg.LeaderId == null ? null : _connections.GetValueOrDefault(msg.LeaderId);
                if (leader == null || leader.OrgId != c.OrgId || !leader.Leading || leader == c)
                {
                    Send(c, new SyncWireMessage { Type = "leaderGone", LeaderId = msg.LeaderId, ServerTimeMs = now });
                    return;
                }
                if (c.Leading)
                {
                    StopLeading(c);
                    SchedulePeers(c.OrgId!.Value);
                }
                c.FollowingId = leader.Id;
                if (leader.LastState is { } last)
                    Send(c, new SyncWireMessage { Type = "state", LeaderId = leader.Id, State = last, ServerTimeMs = now });
                break;
            }
            case "unfollow":
                c.FollowingId = null;
                break;
            case "state" when c.Leading && msg.State != null:
            {
                c.LastState = msg.State;
                var bytes = Serialize(new SyncWireMessage { Type = "state", LeaderId = c.Id, State = msg.State, ServerTimeMs = now });
                foreach (var follower in _connections.Values.Where(f => f.FollowingId == c.Id && f.OrgId == c.OrgId))
                    Enqueue(follower, bytes);
                break;
            }
        }
    }

    private void StopLeading(Connection c)
    {
        c.Leading = false;
        c.LastState = null;
        var gone = Serialize(new SyncWireMessage { Type = "leaderGone", LeaderId = c.Id, ServerTimeMs = MonoClock.NowMs });
        foreach (var follower in _connections.Values.Where(f => f.FollowingId == c.Id))
        {
            follower.FollowingId = null;
            Enqueue(follower, gone);
        }
    }

    private void LeaveOrg(Connection c)
    {
        if (c.OrgId is not { } orgId) return;
        if (c.Leading) StopLeading(c);
        c.FollowingId = null;
        c.OrgId = null;
        SchedulePeers(orgId);
    }

    /// <summary>Peer lists go out at most every 150 ms per organization, however many changes arrive.</summary>
    private void SchedulePeers(long orgId)
    {
        if (!_peerBroadcastPending.TryAdd(orgId, 0)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(PeerBroadcastDelay);
            _peerBroadcastPending.TryRemove(orgId, out _);
            BroadcastPeers(orgId);
        });
    }

    private void BroadcastPeers(long orgId)
    {
        var members = _connections.Values.Where(x => x.OrgId == orgId).ToList();
        if (members.Count == 0) return;
        var peers = members.Select(x => new OnlinePeer
        {
            Id = x.Id,
            Name = x.Name,
            UserName = x.UserName,
            Platform = x.Platform,
            ServerBookId = x.ServerBookId,
            BookUid = x.BookUid,
            BookTitle = x.BookTitle,
            Leading = x.Leading
        }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var bytes = Serialize(new SyncWireMessage { Type = "peers", Peers = peers, ServerTimeMs = MonoClock.NowMs });
        foreach (var m in members) Enqueue(m, bytes);
    }

    private static byte[] Serialize(SyncWireMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, OnlineJson.Options);

    private static void Send(Connection c, SyncWireMessage message) => Enqueue(c, Serialize(message));

    private static void Enqueue(Connection c, byte[] bytes)
    {
        if (!c.Outbox.Writer.TryWrite(bytes)) c.Socket.Abort(); // queue full: this client is not reading
    }
}
