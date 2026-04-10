using System.Runtime.InteropServices;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent.Services;

/// <summary>Injects mouse and keyboard input via SendInput.</summary>
public class InputService
{
    public void MoveMouse(MouseMovePayload p)
    {
        var screen = GetScreenSize();
        // SendInput expects normalised coords 0–65535
        int nx = (int)((double)p.X / screen.Width  * 65535);
        int ny = (int)((double)p.Y / screen.Height * 65535);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u    = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx          = nx,
                    dy          = ny,
                    dwFlags     = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    public void Click(MouseClickPayload p)
    {
        MoveMouse(new MouseMovePayload { X = p.X, Y = p.Y });
        uint flags = p.Button switch
        {
            0 => p.IsDown ? MOUSEEVENTF_LEFTDOWN  : MOUSEEVENTF_LEFTUP,
            1 => p.IsDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => p.IsDown ? MOUSEEVENTF_MIDDLEDOWN: MOUSEEVENTF_MIDDLEUP,
            _ => p.IsDown ? MOUSEEVENTF_LEFTDOWN  : MOUSEEVENTF_LEFTUP
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u    = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    public void KeyEvent(KeyPressPayload p)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u    = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk     = (ushort)p.VirtualKey,
                    dwFlags = p.IsDown ? 0u : KEYEVENTF_KEYUP
                }
            }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private static (int Width, int Height) GetScreenSize()
    {
        int w = GetSystemMetrics(0);
        int h = GetSystemMetrics(1);
        return (w, h);
    }

    // ── Win32 ──────────────────────────────────────────────────────────────
    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN  = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP    = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP         = 0x0002;

    [DllImport("user32.dll")] private static extern uint  SendInput(uint nInputs, ref INPUT pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern int   GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT    { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT    { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    mi;
        [FieldOffset(0)] public KEYBDINPUT    ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
}
