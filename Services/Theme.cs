using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipNest.Services;

public static class Theme
{
    public static bool IsDark { get; private set; } = true;
    private static (bool dark, Color accent)? _applied;

    /// <summary>Swaps the palette. Skipped when nothing changed: Windows raises UserPreferenceChanged
    /// for all kinds of settings, and a palette swap re-resolves every brush in every window.</summary>
    public static void Apply(ThemeMode mode)
    {
        bool dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };
        var accentColor = SystemAccent();
        if (_applied == (dark, accentColor)) return;
        _applied = (dark, accentColor);
        IsDark = dark;

        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        // App.xaml keeps the palette at index 0 and the control styles after it
        dicts[0] = palette;

        var accent = accentColor;
        SetBrush("Accent", accent);
        SetBrush("AccentSubtle", Color.FromArgb((byte)(IsDark ? 0x40 : 0x2A), accent.R, accent.G, accent.B));
        SetBrush("AccentText", IsDark ? Lighten(accent, 0.35) : Darken(accent, 0.15));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    private static void SetBrush(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch { return true; }
    }

    private static Color SystemAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
            {
                var u = unchecked((uint)abgr);
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch { }
        return Color.FromRgb(0x4F, 0x7C, 0xFF);
    }

    private static Color Lighten(Color c, double k) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    private static Color Darken(Color c, double k) =>
        Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ClipNest";

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                k.SetValue(Name, $"\"{exe}\" --tray");
            else
                k.DeleteValue(Name, false);
        }
        catch (Exception ex)
        {
            Log.Write("autostart: " + ex.Message);
        }
    }
}
