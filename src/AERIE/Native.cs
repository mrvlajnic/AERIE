using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AERIE;

static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);

    public const int WM_MOUSEHWHEEL = 0x020E;

    public static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    public static bool CtrlHeld => Held(0x11);
    public static bool ShiftHeld => Held(0x10);
    public static bool AltHeld => Held(0x12);

    /// The title bar in the window's own colours: dark when the window is,
    /// and on Windows 11, the frame's grey rather than the accent.
    public static void PaintFrame(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 19, ref dark, 4);   // the same, before Windows 10 20H1
        var c = Theme.FrameColor;
        int colorref = c.R | (c.G << 8) | (c.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref colorref, 4); // DWMWA_CAPTION_COLOR (Windows 11)
        int ink = Theme.InkColor.R | (Theme.InkColor.G << 8) | (Theme.InkColor.B << 16);
        DwmSetWindowAttribute(hwnd, 36, ref ink, 4);      // DWMWA_TEXT_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref colorref, 4); // DWMWA_BORDER_COLOR
    }

    public static void BringForward(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        w.Activate();
        SetForegroundWindow(hwnd);
    }
}
