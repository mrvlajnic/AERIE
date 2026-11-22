using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AERIE;

/// The handful of pieces every surface is built from, drawn in code.
static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static class Glyph
    {
        public const string Back = "", Forward = "", Reload = "", Add = "",
            Close = "", Settings = "", Puzzle = "", Lock = "", Warning = "",
            Speaker = "", Mute = "", Search = "", More = "", Pin = "",
            Private = "", Reader = "", Globe = "", History = "", Hide = "",
            Left = "", Right = "", Sidebar = "";
    }

    public static TextBlock Text(string s, double size = 13, Brush? fg = null, FontWeight? weight = null)
        => new()
        {
            Text = s, FontSize = size, FontFamily = Font, Foreground = fg ?? Theme.Ink,
            FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static TextBlock Icon(string glyph, double size = 12, Brush? fg = null)
        => new()
        {
            Text = glyph, FontSize = size, FontFamily = Icons, Foreground = fg ?? Theme.Muted,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };

    /// Anything that can be clicked: a grey under the pointer, nothing at rest.
    public static Border Button(UIElement content, Action click, string? tip = null,
        Thickness? pad = null, Brush? rest = null, Brush? hover = null, double radius = 6)
    {
        var b = new ClickableBorder
        {
            Child = content, Padding = pad ?? new Thickness(6), CornerRadius = new CornerRadius(radius),
            Background = rest ?? Brushes.Transparent, Cursor = Cursors.Arrow, ToolTip = tip,
            Activate = click,
        };
        Accessible(b, tip ?? (content as TextBlock)?.Text);
        b.MouseEnter += (_, _) => b.Background = hover ?? Theme.Hover;
        b.MouseLeave += (_, _) => b.Background = rest ?? Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; b.Activate?.Invoke(); };
        return b;
    }

    /// Makes a border keyboard- and screen-reader-usable: Tab reaches it,
    /// Enter or Space presses it, and a glow shows where the focus is.
    public static void Accessible(ClickableBorder b, string? name)
    {
        b.Focusable = true;
        KeyboardNavigation.SetIsTabStop(b, true);
        AutomationProperties.SetName(b, string.IsNullOrWhiteSpace(name) ? "Button" : name);
        b.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; b.Activate?.Invoke(); }
        };
        b.MouseLeftButtonDown += (_, _) => b.Focus();
        b.GotKeyboardFocus += (_, _) => b.Effect = new DropShadowEffect
        {
            Color = (Theme.Accent as SolidColorBrush)?.Color ?? Colors.Orange,
            BlurRadius = 9, ShadowDepth = 0, Opacity = 0.95,
        };
        b.LostKeyboardFocus += (_, _) => b.Effect = null;
    }

    public static Border IconButton(string glyph, Action click, string? tip = null, double size = 12, Brush? hover = null)
        => Button(Icon(glyph, size), click, tip, new Thickness(7, 6, 7, 6), hover: hover);

    /// A pill: the panel buttons.
    public static Border Pill(string label, Action click, bool filled = false)
    {
        var rest = filled ? Theme.Ink : Theme.Wash;
        var t = Text(label, 12.5, filled ? Theme.Ground : Theme.Ink);
        return Button(t, click, pad: new Thickness(12, 5, 12, 6), rest: rest,
            hover: filled ? rest : Theme.PinLive, radius: 14);
    }

    public static Border Card(UIElement child, double radius = 10) => new()
    {
        Child = child, Background = Theme.Ground, CornerRadius = new CornerRadius(radius),
        BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1),
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = Theme.Dark ? 0.5 : 0.16, Direction = 270 },
        Margin = new Thickness(18),
    };

    public static Border Rule() => new() { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 6, 0, 6) };

    public static TextBox Field(string placeholder, double size = 14)
    {
        var box = new TextBox
        {
            FontSize = size, FontFamily = Font, Foreground = Theme.Ink, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), CaretBrush = Theme.Ink, SelectionBrush = Theme.Accent,
            Padding = new Thickness(2, 4, 2, 4), VerticalContentAlignment = VerticalAlignment.Center,
            Tag = placeholder,
        };
        return box;
    }

    /// A field with its placeholder drawn behind it.
    public static Grid Placeheld(TextBox box, string placeholder)
    {
        var hint = Text(placeholder, box.FontSize, Theme.Muted);
        hint.Margin = new Thickness(4, 0, 0, 0);
        hint.IsHitTestVisible = false;
        // The field may already hold an address when the panel is built.
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var g = new Grid();
        g.Children.Add(hint);
        g.Children.Add(box);
        return g;
    }

    public static ContextMenu Menu(params (string Label, Action? Act)[] items)
    {
        var m = new ContextMenu();
        foreach (var (label, act) in items)
        {
            if (label == "-") { m.Items.Add(new Separator()); continue; }
            var i = new MenuItem { Header = label, IsEnabled = act != null };
            if (act != null) i.Click += (_, _) => act();
            m.Items.Add(i);
        }
        return m;
    }
}

/// A border that behaves like a button: focusable, Tab-reachable, pressed by
/// Enter or Space, and exposed to assistive tech as a Button with Invoke.
sealed class ClickableBorder : Border
{
    public Action? Activate;
    protected override AutomationPeer OnCreateAutomationPeer() => new ClickablePeer(this);
}

sealed class ClickablePeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    public ClickablePeer(ClickableBorder owner) : base(owner) { }
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    protected override string GetClassNameCore() => "Button";
    public override object? GetPattern(PatternInterface pattern) =>
        pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
    public void Invoke() => ((ClickableBorder)Owner).Activate?.Invoke();
}
