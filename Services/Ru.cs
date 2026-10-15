using System;
using System.Globalization;

namespace ClipNest.Services;

/// <summary>Russian formatting helpers: plurals, sizes, relative time.</summary>
public static class Ru
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    public static string Plural(long n, string one, string few, string many)
    {
        var m10 = n % 10; var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? one
            : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few
            : many;
        return $"{n.ToString("N0", Culture)} {word}";
    }

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} Б",
        < 1024 * 1024 => $"{b / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} МБ",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} ГБ",
    };

    public static string TimeAgo(DateTime t)
    {
        var now = DateTime.Now;
        var d = now - t;
        if (d.TotalSeconds < 45) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин назад";
        if (t.Date == now.Date) return $"{(int)d.TotalHours} ч назад";
        if (t.Date == now.Date.AddDays(-1)) return "вчера, " + t.ToString("HH:mm", Culture);
        if (t.Year == now.Year) return t.ToString("d MMM, HH:mm", Culture);
        return t.ToString("d MMM yyyy", Culture);
    }
}
