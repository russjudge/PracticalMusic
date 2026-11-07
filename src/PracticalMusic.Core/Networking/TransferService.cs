using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.Core.Networking;

public sealed class TransferOffer
{
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public IPAddress From { get; set; } = IPAddress.None;
    /// <summary>True when we asked this peer for its book, so no confirmation is needed.</summary>
    public bool WasRequested { get; set; }
}

public sealed class TransferRequest
{
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public IPAddress From { get; set; } = IPAddress.None;
    public int SenderTransferPort { get; set; }
}

internal sealed class TransferHeader
{
    public string Type { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public int SenderTransferPort { get; set; }
}

internal sealed class TransferReply
{
    public bool Accept { get; set; }
    public bool Ok { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// TCP file transfer between instances. Offers are confirmed by the receiving user before any
/// bytes flow; requests ask a peer to send its current book back.
/// </summary>
public sealed class TransferService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _localId;
    private readonly Func<string> _localName;
    private readonly string _incomingDir;
    private readonly HashSet<string> _expectingFrom = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Default TCP port for transfers. Fixed so that firewall rules can be written for it.</summary>
    public const int DefaultPort = 47313;
    /// <summary>How many consecutive ports (preferred, +1, +2, …) are tried before falling back to an ephemeral port.</summary>
    public const int PortAttempts = 10;

    /// <summary>The port actually bound. Differs from <see cref="PreferredPort"/> when that one was in use.</summary>
    public int Port { get; private set; }
    /// <summary>The port asked for in <see cref="Start"/>; 0 means "any free port".</summary>
    public int PreferredPort { get; private set; }

    /// <summary>Asks the user whether to accept an incoming book. Null handler rejects everything.</summary>
    public Func<TransferOffer, Task<bool>>? OfferHandler { get; set; }

    /// <summary>Asks the user whether to send our current book to the requester. Return the path of a package to send, or null to decline.</summary>
    public Func<TransferRequest, Task<string?>>? RequestHandler { get; set; }

    public event Action<TransferOffer, string>? FileReceived;
    public event Action<TransferOffer, double>? ReceiveProgress;
    public event Action<string>? Log;

    public TransferService(string localId, Func<string> localName, string incomingDir)
    {
        _localId = localId;
        _localName = localName;
        _incomingDir = incomingDir;
    }

    /// <summary>
    /// Starts listening on <paramref name="preferredPort"/>; if that is busy the next <see cref="PortAttempts"/> - 1
    /// ports are tried, then any free port. Pass 0 for any free port straight away. <see cref="Port"/> holds the
    /// result, which is what peers are told in the discovery packet.
    /// </summary>
    public void Start(int preferredPort = DefaultPort)
    {
        if (_listener != null) return;
        Directory.CreateDirectory(_incomingDir);
        PreferredPort = Math.Clamp(preferredPort, 0, ushort.MaxValue);
        _cts = new CancellationTokenSource();
        _listener = Bind(PreferredPort);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        if (PreferredPort != 0 && Port != PreferredPort)
            Log?.Invoke($"transfer port {PreferredPort} is in use; listening on {Port} instead");
        _ = AcceptLoop(_cts.Token);
    }

    private static TcpListener Bind(int preferredPort)
    {
        if (preferredPort > 0)
        {
            for (var i = 0; i < PortAttempts && preferredPort + i <= ushort.MaxValue; i++)
            {
                var listener = CreateListener(preferredPort + i);
                try { listener.Start(); return listener; }
                catch (SocketException) { listener.Stop(); }
            }
        }
        var any = CreateListener(0);
        any.Start();
        return any;
    }

    private static TcpListener CreateListener(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        if (OperatingSystem.IsWindows())
            listener.ExclusiveAddressUse = true; // a second instance must see "in use" and move to the next port
        else
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // allow rebinding right after a restart
        return listener;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Log?.Invoke("accept: " + ex.Message); continue; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var from = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var header = await ReadFrameAsync<TransferHeader>(stream, ct) ?? throw new InvalidDataException("empty header");

                switch (header.Type)
                {
                    case "offer":
                        await HandleOfferAsync(stream, header, from, ct);
                        break;
                    case "request":
                        await HandleRequestAsync(stream, header, from, ct);
                        break;
                    default:
                        await WriteFrameAsync(stream, new TransferReply { Accept = false, Reason = "unknown type" }, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"transfer from {from}: {ex.Message}");
            }
        }
    }

    private async Task HandleOfferAsync(NetworkStream stream, TransferHeader header, IPAddress from, CancellationToken ct)
    {
        var offer = new TransferOffer
        {
            SenderId = header.SenderId,
            SenderName = header.SenderName,
            Title = header.Title,
            FileName = header.FileName,
            Size = header.Size,
            From = from
        };
        lock (_expectingFrom) offer.WasRequested = _expectingFrom.Remove(header.SenderId);

        var accept = offer.WasRequested || (OfferHandler != null && await OfferHandler(offer));
        await WriteFrameAsync(stream, new TransferReply { Accept = accept, Reason = accept ? null : "declined" }, ct);
        if (!accept) return;

        var target = UniquePath(Path.Combine(_incomingDir, BookPackage.Sanitize(string.IsNullOrWhiteSpace(header.FileName) ? "book.pmbook" : header.FileName)));
        var temp = target + ".part";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long remaining = header.Size;
                long done = 0;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read <= 0) throw new EndOfStreamException("sender closed the connection early");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                    done += read;
                    ReceiveProgress?.Invoke(offer, header.Size > 0 ? (double)done / header.Size : 1);
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            throw;
        }

        await WriteFrameAsync(stream, new TransferReply { Ok = true }, ct);
        FileReceived?.Invoke(offer, target);
    }

    private async Task HandleRequestAsync(NetworkStream stream, TransferHeader header, IPAddress from, CancellationToken ct)
    {
        var request = new TransferRequest
        {
            SenderId = header.SenderId,
            SenderName = header.SenderName,
            From = from,
            SenderTransferPort = header.SenderTransferPort
        };
        var path = RequestHandler == null ? null : await RequestHandler(request);
        var accept = !string.IsNullOrEmpty(path) && File.Exists(path);
        await WriteFrameAsync(stream, new TransferReply { Accept = accept, Reason = accept ? null : "declined" }, ct);
        if (!accept) return;

        // Send the package back on a fresh connection so the requester can treat it like any offer.
        _ = Task.Run(async () =>
        {
            try { await SendFileAsync(from, header.SenderTransferPort, path!, Path.GetFileNameWithoutExtension(path!), null, CancellationToken.None); }
            catch (Exception ex) { Log?.Invoke("reply send failed: " + ex.Message); }
        }, CancellationToken.None);
    }

    /// <summary>Offers a package to a peer. Returns true when the peer accepted and received everything.</summary>
    public async Task<bool> SendFileAsync(IPAddress address, int port, string filePath, string title,
        IProgress<double>? progress, CancellationToken ct)
    {
        var fi = new FileInfo(filePath);
        using var client = new TcpClient();
        client.NoDelay = true;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(address, port, connectCts.Token);
        }
        await using var stream = client.GetStream();
        await WriteFrameAsync(stream, new TransferHeader
        {
            Type = "offer",
            SenderId = _localId,
            SenderName = _localName(),
            Title = title,
            FileName = fi.Name,
            Size = fi.Length,
            SenderTransferPort = Port
        }, ct);

        // The receiving user may take a while to answer the prompt.
        var reply = await ReadFrameAsync<TransferReply>(stream, ct, TimeSpan.FromMinutes(5));
        if (reply == null || !reply.Accept) return false;

        await using (var input = File.OpenRead(filePath))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(fi.Length > 0 ? (double)done / fi.Length : 1);
            }
        }
        await stream.FlushAsync(ct);
        var final = await ReadFrameAsync<TransferReply>(stream, ct, TimeSpan.FromMinutes(2));
        return final?.Ok == true;
    }

    /// <summary>Asks a peer to send its current book. Returns true when the peer agreed; the file then arrives as an auto-accepted offer.</summary>
    public async Task<bool> RequestBookAsync(string peerId, IPAddress address, int port, CancellationToken ct)
    {
        lock (_expectingFrom) _expectingFrom.Add(peerId);
        try
        {
            using var client = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(address, port, connectCts.Token);
            }
            await using var stream = client.GetStream();
            await WriteFrameAsync(stream, new TransferHeader
            {
                Type = "request",
                SenderId = _localId,
                SenderName = _localName(),
                SenderTransferPort = Port
            }, ct);
            var reply = await ReadFrameAsync<TransferReply>(stream, ct, TimeSpan.FromMinutes(5));
            var ok = reply?.Accept == true;
            if (!ok) lock (_expectingFrom) _expectingFrom.Remove(peerId);
            return ok;
        }
        catch
        {
            lock (_expectingFrom) _expectingFrom.Remove(peerId);
            throw;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        await stream.WriteAsync(len, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var len = new byte[4];
        await stream.ReadExactlyAsync(len, cts.Token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (size <= 0 || size > 1 << 20) throw new InvalidDataException("bad frame size");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cts.Token);
        return JsonSerializer.Deserialize<T>(payload, Json);
    }

    public void Dispose() => Stop();
}
