using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;

namespace ClipNest.Services;

/// <summary>The interface language. <see cref="Unset"/>: not chosen yet; on the first start ClipNest picks one from
/// the Windows languages (<see cref="Loc.FromWindows"/>) and saves it, after that it changes only in the settings.</summary>
public enum UiLanguage { Unset, Russian, English, Chinese }

/// <summary>The current interface language: its strings (<see cref="Strings"/>), plurals, sizes and dates.</summary>
public static class Loc
{
    private static Dictionary<string, string> _strings = Strings.For("en");

    /// <summary>"ru", "en" or "zh" (Simplified Chinese).</summary>
    public static string Code { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    /// <summary>For FrameworkElement.Language: picks the Chinese (not Japanese) forms of Han characters, among others.</summary>
    public static XmlLanguage Xml => XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

    /// <summary>Raised after <see cref="Apply"/> switched the language, for text set from code (the tray menu).</summary>
    public static event Action? Changed;

    /// <summary>Switches the language: the {DynamicResource s.…} texts of every window update at once.</summary>
    public static void Apply(UiLanguage language)
    {
        if (language == UiLanguage.Unset) language = FromWindows();
        Code = language switch { UiLanguage.Russian => "ru", UiLanguage.Chinese => "zh", _ => "en" };
        Culture = CultureInfo.GetCultureInfo(Code switch { "ru" => "ru-RU", "zh" => "zh-CN", _ => "en-US" });
        _strings = Strings.For(Code);
        if (Application.Current is { } app)
        {
            foreach (var (key, text) in _strings) app.Resources["s." + key] = text;
            foreach (Window w in app.Windows) w.Language = Xml;
        }
        Changed?.Invoke();
    }

    /// <summary>The first of the user's languages that ClipNest has: the Windows display language, then the preferred
    /// languages in their order (Settings → Time &amp; language → Language). English if none of them is Russian,
    /// English or Chinese. Any Chinese (zh-CN, zh-TW, zh-Hant…) gets Simplified Chinese.</summary>
    public static string Detect(IEnumerable<string> languages)
    {
        foreach (var tag in languages)
        {
            var t = tag.Trim().ToLowerInvariant();
            foreach (var code in new[] { "ru", "en", "zh" })
                if (t == code || t.StartsWith(code + "-")) return code;
        }
        return "en";
    }

    /// <summary>The language to start with on this computer, see <see cref="Detect"/>.</summary>
    public static UiLanguage FromWindows() => Detect(SystemLanguages()) switch
    {
        "ru" => UiLanguage.Russian,
        "zh" => UiLanguage.Chinese,
        _ => UiLanguage.English,
    };

    private static IEnumerable<string> SystemLanguages()
    {
        yield return CultureInfo.CurrentUICulture.Name;
        // the user's own list first: the API below also returns the fallbacks Windows adds (often en-US) up front
        string[]? profile = null;
        try { profile = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile")?.GetValue("Languages") as string[]; }
        catch { }
        foreach (var l in profile ?? Array.Empty<string>()) yield return l;
        foreach (var l in PreferredUiLanguages()) yield return l;
    }

    private static string[] PreferredUiLanguages()
    {
        try
        {
            uint size = 0;
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref size) || size == 0) return Array.Empty<string>();
            var buf = new char[size];
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, buf, ref size)) return Array.Empty<string>();
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return Array.Empty<string>(); }
    }

    private const uint MUI_LANGUAGE_NAME = 0x8;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, char[]? buffer, ref uint size);

    public static string T(string key) => _strings.TryGetValue(key, out var s) ? s : key;

    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);

    /// <summary>"5 записей", "5 items", "5 条记录": the number and the plural form of <paramref name="key"/>.</summary>
    public static string Count(long n, string key) => $"{n.ToString("N0", Culture)} {Form(n, T(key))}";

    private static string Form(long n, string forms)
    {
        var f = forms.Split('|');
        if (f.Length == 3) // Russian: one, few, many
        {
            long m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? f[0] : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? f[1] : f[2];
        }
        return f.Length == 2 ? (n == 1 ? f[0] : f[1]) : f[0];
    }

    public static string Bytes(long b)
    {
        var u = T("ByteUnits").Split('|');
        return b switch
        {
            < 1024 => $"{b} {u[0]}",
            < 1024 * 1024 => (b / 1024.0).ToString("0.#", Culture) + " " + u[1],
            < 1024L * 1024 * 1024 => (b / 1024.0 / 1024).ToString("0.#", Culture) + " " + u[2],
            _ => (b / 1024.0 / 1024 / 1024).ToString("0.##", Culture) + " " + u[3],
        };
    }

    public static string TimeAgo(DateTime t)
    {
        var now = DateTime.Now;
        var d = now - t;
        if (d.TotalSeconds < 45) return T("JustNow");
        if (d.TotalMinutes < 60) return F("MinutesAgo", (int)d.TotalMinutes);
        if (t.Date == now.Date) return F("HoursAgo", (int)d.TotalHours);
        if (t.Date == now.Date.AddDays(-1)) return F("Yesterday", t.ToString("HH:mm", Culture));
        return t.ToString(T(t.Year == now.Year ? "DateThisYear" : "DateOlder"), Culture);
    }
}
