using System.Windows.Media;
using Microsoft.Win32;

namespace Search;

/// Search's greys (Design.swift), each a pair: one for a light window, one
/// for a dark. The window follows Settings › Appearance: light, dark, or
/// whatever Windows is doing.
static class Theme
{
    public static bool Dark { get; private set; }
    public static string Look { get; private set; } = "system";
    public static event Action? Changed;

    public static SolidColorBrush Ground = null!, Ink = null!, Muted = null!, Faint = null!, Hairline = null!,
        Wash = null!, PinLive = null!, Hover = null!, Frame = null!, SideWash = null!, SideHover = null!,
        SideTile = null!, SidePinLive = null!, Safe = null!, Unsafe = null!, Accent = null!, Shade = null!;
    public static Color GroundColor, FrameColor, InkColor;

    static Color Grey(double light, double dark)
    {
        var v = (byte)Math.Round((Dark ? dark : light) * 255);
        return Color.FromRgb(v, v, v);
    }

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Look == "system" && e.Category == UserPreferenceCategory.General)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply(Look));
        };
        Apply("system");
    }

    public static bool SystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(string look)
    {
        Look = look;
        bool dark = look == "dark" || (look == "system" && SystemDark());
        Dark = dark;
        GroundColor = Grey(1.0, 0.11);
        FrameColor = Grey(0.955, 0.075);
        InkColor = Grey(0.09, 0.93);
        Ground = B(GroundColor);
        Ink = B(InkColor);
        Muted = B(Grey(0.55, 0.58));
        Faint = B(Grey(0.83, 0.32));
        Hairline = B(Grey(0.91, 0.20));
        Wash = B(Grey(0.937, 0.175));
        PinLive = B(Grey(0.90, 0.21));
        Hover = B(Grey(0.965, 0.15));
        Frame = B(FrameColor);
        SideWash = B(Grey(0.895, 0.155));
        SideHover = B(Grey(0.915, 0.13));
        SideTile = B(Grey(0.925, 0.115));
        SidePinLive = B(Grey(0.86, 0.19));
        Safe = B(dark ? Color.FromRgb(74, 222, 128) : Color.FromRgb(20, 128, 61));
        Unsafe = B(dark ? Color.FromRgb(250, 191, 36) : Color.FromRgb(181, 84, 10));
        Accent = B(Color.FromRgb(245, 158, 11));
        Shade = B(Color.FromArgb((byte)(dark ? 120 : 40), 0, 0, 0));
        Changed?.Invoke();
    }
}
