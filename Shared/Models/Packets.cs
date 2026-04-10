using System.IO;
using System.Text.Json.Serialization;

namespace NytroxRAT.Shared.Models;

public enum PacketType
{
    // Handshake
    AgentRegister,
    AgentRegistered,
    ClientConnect,
    ClientConnected,
    Disconnect,
    Ping,
    Pong,

    // Screen
    ScreenFrame,
    ScreenRequest,

    // Input
    MouseMove,
    MouseClick,
    KeyPress,

    // File
    FileListRequest,
    FileListResponse,
    FileDownloadRequest,
    FileDownloadResponse,
    FileUploadRequest,
    FileUploadResponse,
    FileDeleteRequest,
    FileDeleteResponse,

    // Command
    CommandExecute,
    CommandOutput,

    // Monitoring
    SystemStats,
    ProcessList,

    // Webcam
    WebcamRequest,
    WebcamFrame,
    WebcamStop,
    WebcamListRequest,
    WebcamListResponse,
}

public class Packet
{
    [JsonPropertyName("type")]
    public PacketType Type { get; set; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("payload")]
    public string Payload { get; set; } = "";  // JSON-serialized inner object

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

// ── Screen ─────────────────────────────────────────────────────────────────
public class ScreenFramePayload
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string ImageBase64 { get; set; } = "";  // JPEG base64
    public int Quality { get; set; } = 50;
}

// ── Input ──────────────────────────────────────────────────────────────────
public class MouseMovePayload { public int X { get; set; } public int Y { get; set; } }
public class MouseClickPayload { public int X { get; set; } public int Y { get; set; } public int Button { get; set; } public bool IsDown { get; set; } }
public class KeyPressPayload { public int VirtualKey { get; set; } public bool IsDown { get; set; } }

// ── Files ──────────────────────────────────────────────────────────────────
public class FileListRequest { public string Path { get; set; } = @"C:\"; }
public class FileListResponse
{
    public string Path { get; set; } = "";
    public List<FileEntry> Entries { get; set; } = new();
    public string? Error { get; set; }
}
public class FileEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
}
public class FileDownloadRequest { public string Path { get; set; } = ""; }
public class FileDownloadResponse { public string Path { get; set; } = ""; public string? DataBase64 { get; set; } public string? Error { get; set; } }
public class FileUploadRequest { public string DestinationPath { get; set; } = ""; public string DataBase64 { get; set; } = ""; }
public class FileUploadResponse { public bool Success { get; set; } public string? Error { get; set; } }
public class FileDeleteRequest { public string Path { get; set; } = ""; }
public class FileDeleteResponse { public bool Success { get; set; } public string? Error { get; set; } }

// ── Command ────────────────────────────────────────────────────────────────
public class CommandExecutePayload { public string Command { get; set; } = ""; }
public class CommandOutputPayload { public string Output { get; set; } = ""; public bool IsError { get; set; } public bool IsFinished { get; set; } }

// ── Monitoring ─────────────────────────────────────────────────────────────
public class SystemStatsPayload
{
    public double CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public string Hostname { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
}
public class DiskInfo { public string Name { get; set; } = ""; public long TotalBytes { get; set; } public long FreeBytes { get; set; } }
public class ProcessEntry { public int Pid { get; set; } public string Name { get; set; } = ""; public double CpuPercent { get; set; } public long MemoryBytes { get; set; } }

// ── Handshake ──────────────────────────────────────────────────────────────
public class WebcamRequestPayload { public int CameraIndex { get; set; } = 0; }
public class WebcamFramePayload   { public string ImageBase64 { get; set; } = ""; public int Width { get; set; } public int Height { get; set; } }
public class WebcamDevice       { public int Index { get; set; } public string Name { get; set; } = ""; }
public class WebcamListResponse { public List<WebcamDevice> Devices { get; set; } = new(); }
