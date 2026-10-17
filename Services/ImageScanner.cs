using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
#if OCR
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
#endif

namespace ClipNest.Services;

/// <summary>What OCR found on an image: its text (for search) and the secrets on it, in image pixels.</summary>
public sealed record ImageScan(string Text, IReadOnlyList<Int32Rect> Secrets, bool WholeImage, double Milliseconds);

/// <summary>Finds secrets on images with the OCR built into Windows (Windows.Media.Ocr): local, no models of our own.
/// Only in the OCR edition; the other one compiles to a stub that reports itself unavailable.</summary>
public static class ImageScanner
{
#if OCR
    /// <summary>The user's language (reads labels like «Пароль:» and «Карта») plus English when it differs:
    /// the Russian recognizer misreads Latin tokens («Token» → «ТоКеп»), the English one can't read Cyrillic labels.
    /// Their findings are combined.</summary>
    private static readonly Lazy<OcrEngine[]> Engines = new(() =>
    {
        var list = new List<OcrEngine>();
        try
        {
            if (OcrEngine.TryCreateFromUserProfileLanguages() is { } user) list.Add(user);
            var en = new Windows.Globalization.Language("en-US");
            if (!list.Any(e => e.RecognizerLanguage.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                && OcrEngine.IsLanguageSupported(en) && OcrEngine.TryCreateFromLanguage(en) is { } eng)
                list.Add(eng);
        }
        catch (Exception ex) { Log.Write("OCR unavailable: " + ex.Message); }
        return list.ToArray();
    });
    private static readonly SemaphoreSlim Gate = new(1, 1); // one recognition at a time
    private const int TileOverlap = 160;

    public static bool Available => Engines.Value.Length > 0;
    public static string? Language => Available ? string.Join(", ", Engines.Value.Select(e => e.RecognizerLanguage.LanguageTag)) : null;

    /// <summary>Recognizes text on a Bgra32 image. Call off the UI thread; takes ~0.1–0.5 s for a screenshot.</summary>
    public static async Task<ImageScan?> ScanAsync(byte[] bgra, int width, int height)
    {
        var engines = Engines.Value;
        if (engines.Length == 0 || width <= 0 || height <= 0) return null;
        int max = (int)OcrEngine.MaxImageDimension;
        if (width > max) return null; // wider than 10 000 px: not a screenshot worth reading

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = new StringBuilder();
        var rects = new List<Int32Rect>();
        bool whole = false;
        int stride = width * 4;

        await Gate.WaitAsync();
        try
        {
            // tall scrolling screenshots: horizontal bands with an overlap, so no line is cut in two everywhere
            for (int top = 0; top < height; top += max - TileOverlap)
            {
                int bandH = Math.Min(max, height - top);
                var buffer = bgra.AsBuffer(top * stride, bandH * stride);
                using var bmp = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, bandH, BitmapAlphaMode.Premultiplied);
                foreach (var engine in engines)
                foreach (var line in (await engine.RecognizeAsync(bmp)).Lines)
                {
                    var words = line.Words;
                    if (engine == engines[0]) text.AppendLine(line.Text); // search text from the user's own language
                    var found = SecretDetector.FindInWords(words.Select(w => w.Text).ToList(), out bool lineWhole);
                    whole |= lineWhole;
                    foreach (var (first, last) in found)
                    {
                        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = 0, y2 = 0;
                        for (int i = first; i <= last; i++)
                        {
                            var r = words[i].BoundingRect;
                            x1 = Math.Min(x1, r.X); y1 = Math.Min(y1, r.Y);
                            x2 = Math.Max(x2, r.X + r.Width); y2 = Math.Max(y2, r.Y + r.Height);
                        }
                        rects.Add(Pad(x1, y1 + top, x2, y2 + top, width, height));
                    }
                }
                if (top + bandH >= height) break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("OCR: " + ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }

        var merged = Merge(rects);
        // too many hits to blur piece by piece: hide the whole picture
        if (merged.Count > 12) whole = true;
        return new ImageScan(text.ToString(), merged, whole, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>A little room around the words, so the blur covers the glyph edges.</summary>
    private static Int32Rect Pad(double x1, double y1, double x2, double y2, int w, int h)
    {
        double pad = Math.Max(4, (y2 - y1) * 0.25);
        int l = (int)Math.Max(0, x1 - pad), t = (int)Math.Max(0, y1 - pad);
        int r = (int)Math.Min(w, x2 + pad), b = (int)Math.Min(h, y2 + pad);
        return new Int32Rect(l, t, Math.Max(1, r - l), Math.Max(1, b - t));
    }

    /// <summary>The same secret found twice in overlapping bands comes out as one rectangle.</summary>
    private static List<Int32Rect> Merge(List<Int32Rect> rects)
    {
        var result = new List<Int32Rect>();
        foreach (var r in rects)
        {
            int i = result.FindIndex(o => o.X < r.X + r.Width && r.X < o.X + o.Width && o.Y < r.Y + r.Height && r.Y < o.Y + o.Height);
            if (i < 0) { result.Add(r); continue; }
            var o = result[i];
            int l = Math.Min(o.X, r.X), t = Math.Min(o.Y, r.Y);
            result[i] = new Int32Rect(l, t, Math.Max(o.X + o.Width, r.X + r.Width) - l, Math.Max(o.Y + o.Height, r.Y + r.Height) - t);
        }
        return result;
    }
#else
    public static bool Available => false;
    public static string? Language => null;
    public static Task<ImageScan?> ScanAsync(byte[] bgra, int width, int height) => Task.FromResult<ImageScan?>(null);
#endif

    /// <summary>Regions as stored in the database: "x,y,w,h;x,y,w,h".</summary>
    public static string? Serialize(IReadOnlyList<Int32Rect> rects) =>
        rects.Count == 0 ? null : string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));

    public static Int32Rect[] Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<Int32Rect>();
        var list = new List<Int32Rect>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var n = part.Split(',');
            if (n.Length == 4 && int.TryParse(n[0], out var x) && int.TryParse(n[1], out var y)
                && int.TryParse(n[2], out var w) && int.TryParse(n[3], out var h) && w > 0 && h > 0)
                list.Add(new Int32Rect(x, y, w, h));
        }
        return list.ToArray();
    }
}
