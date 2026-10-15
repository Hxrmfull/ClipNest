using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Services;

namespace ClipNest.Models;

public enum ClipKind { Text = 0, Link = 1, Image = 2, Files = 3 }

/// <summary>One history entry. Items from the list carry only a preview; the full payload
/// is loaded by <see cref="Storage.LoadFull"/> right before it is pasted.
/// Display strings are computed once and cached: containers are recycled while scrolling.</summary>
public sealed partial class ClipItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public ClipKind Kind { get; set; }
    /// <summary>Full text; null until loaded (list items) and for images.</summary>
    public string? Text { get; set; }
    public string? Html { get; set; }
    public string? Rtf { get; set; }
    public string? Files { get; set; }
    /// <summary>First <see cref="Storage.PreviewChars"/> characters of the text or file list; null for secrets.</summary>
    public string? PreviewText { get; set; }
    /// <summary>The stored item has HTML or RTF (known without loading them).</summary>
    public bool RichFormat { get; set; }
    public bool IsFull { get; set; }
    public string? ImagePath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Hash { get; set; } = "";
    public string? Source { get; set; }
    public DateTime Created { get; set; }
    public DateTime Stamp { get; set; }
    /// <summary>Text: UTF-16 bytes; image: PNG bytes; files: number of files.</summary>
    public long Size { get; set; }
    /// <summary>Password, token, key… Shown as a spoiler and stored encrypted.</summary>
    public bool Secret { get; set; }

    private bool _revealed;
    /// <summary>Spoiler opened by the user; lasts until the list is reloaded.
    /// The caller loads the full text first: secrets have no stored preview.</summary>
    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (_revealed == value) return;
            _revealed = value;
            _preview = null;
            OnChanged();
            OnChanged(nameof(IsHidden));
            OnChanged(nameof(Preview));
            OnChanged(nameof(ToolTipText));
        }
    }

    public bool IsHidden => Secret && !_revealed;

    private bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { if (_pinned != value) { _pinned = value; OnChanged(); } }
    }

    private int _index = -1;
    /// <summary>Position in the visible list, for the Ctrl+1..9 hint.</summary>
    public int Index
    {
        get => _index;
        set { if (_index != value) { _index = value; OnChanged(); OnChanged(nameof(QuickKey)); } }
    }

    public string? QuickKey => _index is >= 0 and < 9 ? $"Ctrl+{_index + 1}" : null;

    public bool IsImage => Kind == ClipKind.Image;
    public bool IsFiles => Kind == ClipKind.Files;
    public bool IsTextual => Kind is ClipKind.Text or ClipKind.Link;
    public bool HasRichFormat => RichFormat || !string.IsNullOrEmpty(Html) || !string.IsNullOrEmpty(Rtf);

    /// <summary>Visible text source: the full text when loaded, else the stored preview.</summary>
    private string Body => (Kind == ClipKind.Files ? Files : Text) ?? PreviewText ?? "";

    public string[] FileList => Kind == ClipKind.Files
        ? Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        : Array.Empty<string>();

    public string KindGlyph => Secret ? "" : Kind switch
    {
        ClipKind.Link => "",
        ClipKind.Image => "",
        ClipKind.Files => "",
        _ => "",
    };

    private string? _preview;
    public string Preview => _preview ??= BuildPreview();

    private string BuildPreview()
    {
        if (IsHidden) return "";
        switch (Kind)
        {
            case ClipKind.Files:
                var files = FileList;
                var names = string.Join("\n", files.Take(4).Select(f => Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f));
                long total = Math.Max(Size, files.Length);
                return total > 4 ? names + $"\n… и ещё {total - 4}" : names;
            case ClipKind.Image:
                return "";
            default:
                var t = Body;
                if (t.Length > 600) t = t[..600] + "…";
                return t.Replace("\t", "    ").Trim('\r', '\n');
        }
    }

    /// <summary>Full-ish text on hover, only when the card can't show it all.</summary>
    public string? ToolTipText
    {
        get
        {
            if (IsHidden || Kind == ClipKind.Image) return null;
            var t = Kind == ClipKind.Files ? string.Join("\n", FileList) : Body;
            if (t.Length <= 200 && t.Count(c => c == '\n') < 4) return null;
            return t.Length > 3000 ? t[..3000] + "\n…" : t;
        }
    }

    private string? _meta;
    public string Meta => _meta ??= BuildMeta();

    private string BuildMeta()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(Source)) parts.Add(Source!);
        switch (Kind)
        {
            case ClipKind.Image:
                parts.Add($"{Width}×{Height}");
                parts.Add(Ru.Bytes(Size));
                if (Secret) parts.Add("скрыто");
                break;
            case ClipKind.Files:
                parts.Add(Ru.Plural(Math.Max(Size, FileList.Length), "файл", "файла", "файлов"));
                break;
            case ClipKind.Text when Secret:
                parts.Add("секретное · зашифровано");
                break;
            default:
                long chars = Size > 0 ? Size / 2 : Body.Length;
                parts.Add(Ru.Plural(chars, "симв.", "симв.", "симв."));
                var body = Body;
                // line count only when the whole text is at hand
                if (body.Length >= chars)
                {
                    int lines = 1;
                    foreach (var c in body) if (c == '\n') lines++;
                    if (lines > 1) parts.Add(Ru.Plural(lines, "строка", "строки", "строк"));
                }
                if (HasRichFormat) parts.Add("формат");
                break;
        }
        return string.Join("  ·  ", parts);
    }

    public string TimeAgo => Ru.TimeAgo(Stamp);

    private bool _swatchDone;
    private Brush? _swatch;
    /// <summary>If the text is a CSS-like color (#RGB, #RRGGBB, rgb()), a brush to show as a swatch.</summary>
    public Brush? ColorSwatch
    {
        get
        {
            if (_swatchDone) return _swatch;
            _swatchDone = true;
            var t = Body.Trim();
            if (Kind != ClipKind.Text || Secret || t.Length is 0 or > 32) return null;
            try
            {
                if (HexColor().IsMatch(t))
                    _swatch = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(t)!));
                else if (RgbColor().Match(t) is { Success: true } m)
                    _swatch = Frozen(new SolidColorBrush(Color.FromRgb(byte.Parse(m.Groups[1].Value), byte.Parse(m.Groups[2].Value), byte.Parse(m.Groups[3].Value))));
            }
            catch { }
            return _swatch;
        }
    }

    public bool HasSwatch => ColorSwatch is not null;

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    private string? _spoiler;
    /// <summary>Gibberish of plausible shape drawn under the blur, so the real text never reaches the screen.</summary>
    public string SpoilerText => _spoiler ??= BuildSpoiler();

    private string BuildSpoiler()
    {
        var rnd = new Random((int)(Id * 7919 % int.MaxValue));
        int lines = Kind == ClipKind.Files ? (int)Math.Clamp(Size, 1, 3) : 1;
        var sb = new StringBuilder();
        for (int l = 0; l < lines; l++)
        {
            if (l > 0) sb.Append('\n');
            int words = rnd.Next(2, 4);
            for (int w = 0; w < words; w++)
            {
                if (w > 0) sb.Append(' ');
                int len = rnd.Next(3, 9);
                for (int i = 0; i < len; i++) sb.Append((char)('a' + rnd.Next(26)));
            }
        }
        return sb.ToString();
    }

    // ---------- images ----------

    /// <summary>Card preview, decoded to fit 760×300 px (the card at up to 200% scale).
    /// Bound with IsAsync, so this runs off the UI thread.</summary>
    public BitmapSource? Thumbnail => ImagePath is null ? null : ThumbCache.Get(ImagePath, () => LoadImage(760, 300));

    /// <summary>Hover preview, decoded to fit 1440×1040 px.</summary>
    public BitmapSource? LargePreview => LoadImage(1440, 1040);

    private BitmapSource? LoadImage(int boxW, int boxH)
    {
        if (Kind != ClipKind.Image || ImagePath is null || !File.Exists(ImagePath)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(Secrets.ReadImage(ImagePath));
            // decode straight to display size: a 1080×9000 screenshot must not cost 40 MB per card
            double scale = Math.Min(1, Math.Min((double)boxW / Math.Max(1, Width), (double)boxH / Math.Max(1, Height)));
            if (scale < 1) bmp.DecodePixelWidth = Math.Max(1, (int)Math.Round(Width * scale));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColor();
}

/// <summary>Small LRU of decoded card thumbnails, so reopening the popup doesn't decode them again.
/// Concurrent requests for the same image share one decode (Lazy).</summary>
internal static class ThumbCache
{
    private const int Capacity = 48;
    private static readonly Dictionary<string, LinkedListNode<(string key, Lazy<BitmapSource?> img)>> Map = new();
    private static readonly LinkedList<(string key, Lazy<BitmapSource?> img)> Order = new();
    private static readonly object Gate = new();

    public static BitmapSource? Get(string key, Func<BitmapSource?> load)
    {
        Lazy<BitmapSource?> entry;
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Order.Remove(node);
                Order.AddFirst(node);
                entry = node.Value.img;
            }
            else
            {
                entry = new Lazy<BitmapSource?>(load, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                Map[key] = Order.AddFirst((key, entry));
                if (Order.Count > Capacity)
                {
                    Map.Remove(Order.Last!.Value.key);
                    Order.RemoveLast();
                }
            }
        }
        return entry.Value;
    }

    public static void Forget(string? key)
    {
        if (key is null) return;
        lock (Gate)
        {
            if (Map.Remove(key, out var node)) Order.Remove(node);
        }
    }
}
