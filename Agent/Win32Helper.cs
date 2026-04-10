using System.Runtime.InteropServices;

namespace NytroxRAT.Agent.Win32;

public static class HideConsole
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void Hide()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0); // SW_HIDE
    }

    // Make it callable as NytroxRAT.Agent.Win32.HideConsole()
    public static void Invoke() => Hide();
}
