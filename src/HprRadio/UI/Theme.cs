using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace HprRadio.UI;

/// <summary>The warm "barn wood and lamplight" palette, light or dark to match Windows.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    private static bool? _forced;

    /// <summary>For testing: light or dark regardless of the Windows setting.</summary>
    public static void Force(bool dark) => _forced = dark;
    public static event Action? Changed;

    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("Bg",         "#FBF6EC", "#1C1611"),
        ("Card",       "#F2E8D5", "#2A221B"),
        ("CardHover",  "#EBDDC3", "#352B22"),
        ("Text",       "#2B1D12", "#EFE2D2"),
        ("SubText",    "#6B5644", "#C9B6A2"),
        ("Accent",     "#8A4B1F", "#E7A955"),
        ("OnAccent",   "#FFFFFF", "#3B2300"),
        ("AccentText", "#8A4B1F", "#E7A955"),
        ("AccentSoft", "#1F8A4B1F", "#29E7A955"),
        ("Divider",    "#E3D5BC", "#3A2F25"),
        ("Track",      "#E0D2B8", "#463A2E"),
        ("NpBar",      "#F2E8D5", "#241D17"),
        ("Placeholder","#F6D58A", "#F6D58A"),
    };

    public static void Apply()
    {
        IsDark = WindowsUsesDarkMode();
        var res = Application.Current.Resources;
        foreach (var (key, light, dark) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
            brush.Freeze();
            res[key] = brush;
        }
        foreach (Window w in Application.Current.Windows) DarkTitleBar(w);
        Changed?.Invoke();
    }

    public static void Watch() =>
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
                && WindowsUsesDarkMode() != IsDark)
                Application.Current.Dispatcher.InvokeAsync(Apply);
        };

    private static bool WindowsUsesDarkMode()
    {
        if (_forced is { } f) return f;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar in dark mode (Windows 10 20H1 and later).</summary>
    public static void DarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>"#8A4B1F" → brush, or the fallback.</summary>
    public static Brush FromHex(string? hex, string fallback = "#5B4636")
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? fallback)); }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }
}
