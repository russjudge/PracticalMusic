using System.Net;
using System.Net.WebSockets;

using System.Text.Json;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Networking;

namespace PracticalMusic.Core.Online;

/// <summary>
/// Synchronized playback through a Practical Music website, for the desktop and Android apps. It is the
/// online counterpart of <see cref="LanBus"/> + <see cref="SyncService"/>: the app appears as a peer of
/// everyone connected to the same organization (browsers included), can lead, and can follow a leader.
/// Only an outbound HTTPS connection is needed, so no firewall port has to be opened on the device.
/// Raises its events on background threads.
/// </summary>
public sealed class OnlineSyncClient : IDisposable
{
    private const int MaxMessageBytes = 1 << 20;

    private readonly ClockOffsetEstimator _clock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private WebSocket? _ws;
    private List<OnlinePeer> _peers = new();
    private Uri? _socketUri;
    private string? _token;

    public OnlineSyncClient(string localName, string platform)
    {
        LocalName = localName;
        Platform = platform;
    }

    public string LocalName { get; set; }
    public string Platform { get; }

    /// <summary>Opens the socket (address, token) instead of a <see cref="ClientWebSocket"/> - used by tests to reach an in-process website.</summary>
    public Func<Uri, string, CancellationToken, Task<WebSocket>>? Connector { get; set; }

    /// <summary>The book that is open on this device (server book id when it came from the website, book.json id, title).</summary>
    public Func<(long? ServerBookId, string? Uid, string? Title)>? BookInfoProvider { get; set; }

    public bool IsRunning => _runCts != null;
    public bool IsConnected { get; private set; }
    public long? OrgId { get; private set; }
    public string? ConnectionId { get; private set; }
    /// <summary>Human-readable connection state ("Connected", "Connecting…", the last error).</summary>
    public string Status { get; private set; } = "Not connected";
    /// <summary>True after the website refused the token: retrying is pointless until the user signs in again.</summary>
    public bool AuthenticationFailed { get; private set; }

    public bool ClockSynced => _clock.IsSynced;
    /// <summary>Server clock minus local clock, in ms.</summary>
    public double ClockOffsetMs => _clock.OffsetMs;
    public double RttMs => _clock.RttMs;

    public IReadOnlyList<OnlinePeer> Peers { get { lock (_gate) return _peers.ToList(); } }
    public OnlinePeer? FindPeer(string id) { lock (_gate) return _peers.FirstOrDefault(p => p.Id == id); }

    public bool Leading { get; private set; }
    public string? FollowingId { get; private set; }
    public bool Following => FollowingId != null;
    public OnlinePeer? Leader => FollowingId == null ? null : FindPeer(FollowingId);
    public string? LeaderName { get; private set; }
    public DateTime FollowingSince { get; private set; }
    public DateTime LastLeaderPacket { get; private set; }
    public SyncState? LastState { get; private set; }

    public event Action? PeersChanged;
    /// <summary>Connection, role or clock changed.</summary>
    public event Action? Changed;
    /// <summary>A followed leader published its state. The second argument is the local monotonic time matching <see cref="SyncState.AtLeaderTimeMs"/>.</summary>
    public event Action<SyncState, double>? StateReceived;
    public event Action<string>? Log;

    /// <summary>Connects (or reconnects) to the website's sync socket for one organization. Retries in the background until <see cref="Stop"/>.</summary>
    public void Start(Uri socketUri, string token, long orgId)
    {
        if (_runCts != null && _socketUri == socketUri && _token == token)
        {
            if (OrgId != orgId)
            {
                OrgId = orgId;
                StopFollowing();
                ClearPeers();
                _ = SendAsync(Hello()); // the server moves this connection to the other organization
            }
            return;
        }
        Stop();
        _socketUri = socketUri;
        _token = token;
        OrgId = orgId;
        AuthenticationFailed = false;
        var cts = _runCts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    public void Stop()
    {
        var cts = _runCts;
        _runCts = null;
        if (cts == null) return;
        cts.Cancel();
        try { _ws?.Abort(); } catch { /* ignore */ }
        _ws = null;
        FollowingId = null;
        LeaderName = null;
        LastState = null;
        IsConnected = false;
        Status = "Not connected";
        ClearPeers();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ roles

    public void StartLeading()
    {
        StopFollowing();
        if (Leading) return;
        Leading = true;
        _ = SendAsync(new SyncWireMessage { Type = "lead" });
        Changed?.Invoke();
    }

    public void StopLeading()
    {
        if (!Leading) return;
        Leading = false;
        _ = SendAsync(new SyncWireMessage { Type = "unlead" });
        Changed?.Invoke();
    }

    public void Follow(string leaderId)
    {
        StopLeading();
        FollowingId = leaderId;
        LeaderName = FindPeer(leaderId)?.Name;
        FollowingSince = DateTime.UtcNow;
        LastState = null;
        _ = SendAsync(new SyncWireMessage { Type = "follow", LeaderId = leaderId });
        Changed?.Invoke();
    }

    public void StopFollowing()
    {
        if (FollowingId == null) return;
        FollowingId = null;
        LeaderName = null;
        LastState = null;
        _ = SendAsync(new SyncWireMessage { Type = "unfollow" });
        Changed?.Invoke();
    }

    /// <summary>Leader: publishes the playback state. <see cref="SyncState.AtLeaderTimeMs"/> is on the local monotonic clock and is converted to server time here.</summary>
    public Task PublishAsync(SyncState state)
    {
        if (!Leading || !IsConnected) return Task.CompletedTask;
        var serverAt = _clock.ToRemote(state.AtLeaderTimeMs);
        if (serverAt == null) return Task.CompletedTask; // no clock estimate yet; the next heartbeat carries the state
        var copy = new SyncState
        {
            SessionId = state.SessionId,
            Seq = state.Seq,
            BookId = state.BookId,
            BookTitle = state.BookTitle,
            SongId = state.SongId,
            SongTitle = state.SongTitle,
            SongIndex = state.SongIndex,
            RecordingId = state.RecordingId,
            RecordingName = state.RecordingName,
            IsPlaying = state.IsPlaying,
            PositionMs = state.PositionMs,
            AtLeaderTimeMs = serverAt.Value
        };
        return SendAsync(new SyncWireMessage { Type = "state", State = copy });
    }

    /// <summary>Tells the others which book is open now.</summary>
    public Task AnnounceBookAsync()
    {
        var info = BookInfoProvider?.Invoke() ?? (null, null, null);
        return SendAsync(new SyncWireMessage { Type = "book", ServerBookId = info.ServerBookId, BookUid = info.Uid, BookTitle = info.Title });
    }

    private SyncWireMessage Hello()
    {
        var info = BookInfoProvider?.Invoke() ?? (null, null, null);
        return new SyncWireMessage
        {
            Type = "hello",
            Protocol = SyncWireMessage.ProtocolVersion,
            OrgId = OrgId,
            Name = LocalName,
            Platform = Platform,
            ServerBookId = info.ServerBookId,
            BookUid = info.Uid,
            BookTitle = info.Title
        };
    }

    // ------------------------------------------------------------------ connection

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            WebSocket? ws = null;
            ClientWebSocket? client = null;
            try
            {
                SetStatus("Connecting…");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    if (Connector != null)
                    {
                        ws = await Connector(_socketUri!, _token!, connect.Token);
                    }
                    else
                    {
                        client = new ClientWebSocket();
                        client.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        client.Options.CollectHttpResponseDetails = true;
                        await client.ConnectAsync(_socketUri!, connect.Token);
                        ws = client;
                    }
                }
                _ws = ws;
                _clock.Reset();
                await SendRawAsync(ws, Hello(), ct);
                if (Leading) await SendRawAsync(ws, new SyncWireMessage { Type = "lead" }, ct);
                if (FollowingId != null) await SendRawAsync(ws, new SyncWireMessage { Type = "follow", LeaderId = FollowingId }, ct);
                delay = TimeSpan.FromSeconds(1);
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = PingLoopAsync(ws, pingCts.Token);
                await ReceiveLoopAsync(ws, ct);
                pingCts.Cancel();
                SetStatus("Connection closed - reconnecting…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex) when (client?.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                AuthenticationFailed = true;
                SetStatus("The website did not accept the sign-in. Sign in again.");
                Log?.Invoke("sync socket refused: " + ex.Message);
                break;
            }
            catch (Exception ex)
            {
                SetStatus("Cannot reach the website - retrying…");
                Log?.Invoke("sync socket: " + ex.Message);
            }
            finally
            {
                if (ws != null && ReferenceEquals(_ws, ws)) _ws = null;
                (ws ?? client)?.Dispose();
                var wasConnected = IsConnected;
                IsConnected = false;
                ClearPeers();
                if (wasConnected) Changed?.Invoke();
            }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    private async Task PingLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var burst = 6;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try { await SendRawAsync(ws, new SyncWireMessage { Type = "ping", T0 = MonoClock.NowMs }, ct); }
            catch { return; }
            try { await Task.Delay(burst-- > 0 ? 300 : 3000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { /* ignore */ }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("sync message too large");
            if (!result.EndOfMessage) continue;
            SyncWireMessage? msg = null;
            try { msg = JsonSerializer.Deserialize<SyncWireMessage>(message.GetBuffer().AsSpan(0, (int)message.Length), OnlineJson.Options); }
            catch (JsonException) { /* ignore garbage */ }
            message.SetLength(0);
            if (msg != null) Handle(msg);
        }
    }

    private void Handle(SyncWireMessage msg)
    {
        var now = MonoClock.NowMs;
        if (msg.ServerTimeMs is { } serverNow && !_clock.IsSynced) _clock.AddCoarse(serverNow, now);
        switch (msg.Type)
        {
            case "welcome":
                ConnectionId = msg.ConnectionId;
                IsConnected = true;
                SetStatus("Connected");
                break;
            case "peers":
                lock (_gate) _peers = (msg.Peers ?? new List<OnlinePeer>()).Where(p => p.Id != ConnectionId).ToList();
                if (FollowingId != null && FindPeer(FollowingId) is { } leader) LeaderName = leader.Name;
                PeersChanged?.Invoke();
                break;
            case "pong" when msg.T0.HasValue && msg.T1.HasValue:
                if (_clock.AddSample(msg.T0.Value, msg.T1.Value, now)) Changed?.Invoke();
                break;
            case "state" when msg.State != null && FollowingId != null && msg.LeaderId == FollowingId:
                LastState = msg.State;
                LastLeaderPacket = DateTime.UtcNow;
                StateReceived?.Invoke(msg.State, _clock.ToLocal(msg.State.AtLeaderTimeMs, now));
                break;
            case "leaderGone" when FollowingId != null && msg.LeaderId == FollowingId:
                Log?.Invoke($"{LeaderName ?? "The leader"} stopped leading.");
                FollowingId = null;
                LeaderName = null;
                LastState = null;
                Changed?.Invoke();
                break;
            case "error":
                Log?.Invoke("website: " + msg.Message);
                break;
        }
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }

    private void ClearPeers()
    {
        bool had;
        lock (_gate)
        {
            had = _peers.Count > 0;
            _peers = new List<OnlinePeer>();
        }
        if (had) PeersChanged?.Invoke();
    }

    private Task SendAsync(SyncWireMessage message)
    {
        var ws = _ws;
        return ws == null || ws.State != WebSocketState.Open ? Task.CompletedTask : SendRawAsync(ws, message, CancellationToken.None);
    }

    private async Task SendRawAsync(WebSocket ws, SyncWireMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, OnlineJson.Options);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
        {
            Log?.Invoke("sync send failed: " + ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose() => Stop();
}
