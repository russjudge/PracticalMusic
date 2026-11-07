using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Core.Networking;

/// <summary>Another instance of Practical Music that was seen on the local network.</summary>
public sealed class PeerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public IPAddress Address { get; set; } = IPAddress.None;
    public int TransferPort { get; set; }
    public int UnicastPort { get; set; }
    public DateTime LastSeen { get; set; }
    public string? BookId { get; set; }
    public string? BookTitle { get; set; }
    public bool Leading { get; set; }
    public string Platform { get; set; } = "";

    public IPEndPoint UnicastEndPoint => new(Address, UnicastPort);
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>Playback state broadcast by a sync leader.</summary>
public sealed class SyncState
{
    public string SessionId { get; set; } = "";
    public long Seq { get; set; }
    public string? BookId { get; set; }
    public string? BookTitle { get; set; }
    public string? SongId { get; set; }
    public string? SongTitle { get; set; }
    public int SongIndex { get; set; } = -1;
    public string? RecordingId { get; set; }
    public string? RecordingName { get; set; }
    public bool IsPlaying { get; set; }
    /// <summary>Position in the recording at <see cref="AtLeaderTimeMs"/>.</summary>
    public double PositionMs { get; set; }
    /// <summary>Leader monotonic clock time (ms) the position refers to. May be in the future for a scheduled start.</summary>
    public double AtLeaderTimeMs { get; set; }
}

/// <summary>Datagram exchanged over UDP. One class with optional fields keeps the protocol simple.</summary>
public sealed class LanPacket
{
    public const string CurrentMagic = "PMUS1";

    public string Magic { get; set; } = CurrentMagic;
    /// <summary>hello, bye, sync, ping, pong</summary>
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>Instance id of the intended recipient; null means everyone. Lets replies travel as broadcasts.</summary>
    public string? To { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public int TransferPort { get; set; }
    public int UnicastPort { get; set; }
    public string? BookId { get; set; }
    public string? BookTitle { get; set; }
    public bool Leading { get; set; }
    public SyncState? Sync { get; set; }
    /// <summary>Clock sync: requester send time.</summary>
    public double T0 { get; set; }
    /// <summary>Clock sync: responder time.</summary>
    public double T1 { get; set; }
}

/// <summary>
/// UDP discovery and messaging. A shared socket on the fixed discovery port (address reuse, so
/// several instances can run on one machine) receives everything; a per-instance socket on an
/// ephemeral port does the sending. All traffic, including the ping/pong exchange that measures the
/// clock offset for synchronized playback, is broadcast and addressed with <see cref="LanPacket.To"/>,
/// so a firewall only has to let inbound UDP <see cref="DiscoveryPort"/> through. The sending socket
/// still accepts unicast replies from older versions.
/// </summary>
public sealed class LanBus : IDisposable
{
    public const int DiscoveryPort = 47312;
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private UdpClient? _broadcast;
    private UdpClient? _unicast;
    private CancellationTokenSource? _cts;

    public string LocalId { get; }
    public string LocalName { get; set; }
    public string Platform { get; set; } = "";
    public int TransferPort { get; set; }
    public bool Leading { get; set; }
    public Func<(string? BookId, string? BookTitle)>? BookInfoProvider { get; set; }
    public int UnicastPort { get; private set; }
    public bool IsRunning => _cts != null;

    public event Action? PeersChanged;
    public event Action<LanPacket, IPEndPoint>? PacketReceived;
    public event Action<string>? Log;

    public LanBus(string localId, string localName)
    {
        LocalId = localId;
        LocalName = localName;
    }

    public IReadOnlyList<PeerInfo> Peers => _peers.Values.OrderBy(p => p.Name).ToList();

    public PeerInfo? FindPeer(string id) => _peers.TryGetValue(id, out var p) ? p : null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        _broadcast = new UdpClient();
        _broadcast.ExclusiveAddressUse = false;
        _broadcast.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _broadcast.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _broadcast.EnableBroadcast = true;
        DisableConnectionReset(_broadcast);

        _unicast = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _unicast.EnableBroadcast = true;
        DisableConnectionReset(_unicast);
        UnicastPort = ((IPEndPoint)_unicast.Client.LocalEndPoint!).Port;

        _ = ReceiveLoop(_broadcast, _cts.Token);
        _ = ReceiveLoop(_unicast, _cts.Token);
        _ = BeaconLoop(_cts.Token);
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { BroadcastAsync(MakePacket("bye")).GetAwaiter().GetResult(); } catch { /* best effort */ }
        _cts = null;
        cts.Cancel();
        _broadcast?.Dispose();
        _unicast?.Dispose();
        _broadcast = null;
        _unicast = null;
        _peers.Clear();
        PeersChanged?.Invoke();
    }

    public LanPacket MakePacket(string type)
    {
        var info = BookInfoProvider?.Invoke() ?? (null, null);
        return new LanPacket
        {
            Type = type,
            Id = LocalId,
            Name = LocalName,
            Platform = Platform,
            TransferPort = TransferPort,
            UnicastPort = UnicastPort,
            BookId = info.BookId,
            BookTitle = info.BookTitle,
            Leading = Leading
        };
    }

    public async Task BroadcastAsync(LanPacket packet)
    {
        var sock = _unicast;
        if (sock == null) return;
        var bytes = Encode(packet);
        foreach (var addr in BroadcastAddresses())
        {
            try { await sock.SendAsync(bytes, bytes.Length, new IPEndPoint(addr, DiscoveryPort)); }
            catch (Exception ex) { Log?.Invoke($"broadcast to {addr} failed: {ex.Message}"); }
        }
    }

    public async Task SendAsync(IPEndPoint target, LanPacket packet)
    {
        var sock = _unicast;
        if (sock == null) return;
        var bytes = Encode(packet);
        try { await sock.SendAsync(bytes, bytes.Length, target); }
        catch (Exception ex) { Log?.Invoke($"send to {target} failed: {ex.Message}"); }
    }

    /// <summary>Sends the beacon right now (e.g. after the book or role changed).</summary>
    public Task AnnounceAsync() => BroadcastAsync(MakePacket("hello"));

    private static byte[] Encode(LanPacket p) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Json));

    private async Task BeaconLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastAsync(MakePacket("hello"));
                PrunePeers();
            }
            catch (Exception ex) { Log?.Invoke("beacon: " + ex.Message); }
            try { await Task.Delay(BeaconInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void PrunePeers()
    {
        var cutoff = DateTime.UtcNow - PeerTimeout;
        var removed = false;
        foreach (var kv in _peers)
            if (kv.Value.LastSeen < cutoff && _peers.TryRemove(kv.Key, out _)) removed = true;
        if (removed) PeersChanged?.Invoke();
    }

    private async Task ReceiveLoop(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            catch (Exception ex) { Log?.Invoke("receive: " + ex.Message); continue; }

            LanPacket? packet;
            try { packet = JsonSerializer.Deserialize<LanPacket>(result.Buffer, Json); }
            catch { continue; }
            if (packet == null || packet.Magic != LanPacket.CurrentMagic || packet.Id == LocalId) continue;
            if (packet.To != null && packet.To != LocalId) continue; // addressed to another instance

            HandlePacket(packet, result.RemoteEndPoint);
        }
    }

    private void HandlePacket(LanPacket packet, IPEndPoint from)
    {
        switch (packet.Type)
        {
            case "hello":
            {
                var isNew = !_peers.TryGetValue(packet.Id, out var peer);
                peer ??= new PeerInfo { Id = packet.Id };
                var changed = isNew || peer.Name != packet.Name || !peer.Address.Equals(from.Address) ||
                              peer.TransferPort != packet.TransferPort || peer.BookTitle != packet.BookTitle ||
                              peer.Leading != packet.Leading || peer.BookId != packet.BookId;
                peer.Name = packet.Name;
                peer.Address = from.Address;
                peer.TransferPort = packet.TransferPort;
                peer.UnicastPort = packet.UnicastPort;
                peer.BookId = packet.BookId;
                peer.BookTitle = packet.BookTitle;
                peer.Leading = packet.Leading;
                peer.Platform = packet.Platform;
                peer.LastSeen = DateTime.UtcNow;
                _peers[packet.Id] = peer;
                if (changed) PeersChanged?.Invoke();
                break;
            }
            case "bye":
                if (_peers.TryRemove(packet.Id, out _)) PeersChanged?.Invoke();
                break;
            default:
                if (_peers.TryGetValue(packet.Id, out var known)) known.LastSeen = DateTime.UtcNow;
                break;
        }
        PacketReceived?.Invoke(packet, from);
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask == null || mask.Equals(IPAddress.Any)) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~m[i]);
                    result.Add(new IPAddress(b));
                }
            }
        }
        catch { /* some platforms restrict interface enumeration; limited broadcast still works */ }
        return result;
    }

    public static IPAddress? LocalIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static void DisableConnectionReset(UdpClient client)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch { /* not critical */ }
    }

    public void Dispose() => Stop();
}
