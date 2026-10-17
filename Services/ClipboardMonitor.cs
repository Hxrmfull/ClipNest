using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipNest.Models;

namespace ClipNest.Services;

/// <summary>Listens for WM_CLIPBOARDUPDATE and turns clipboard contents into history items.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int MaxRichFormatChars = 5 * 1024 * 1024;

    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly HwndSource _hwnd;
    private readonly DispatcherTimer _debounce;
    private uint _ignoreSeq;
    private long _ignoreUntil;
    private bool _busy, _pending;
    private static readonly System.Collections.Generic.Dictionary<string, string> SourceNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly uint FmtExclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtViewerIgnore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint FmtCanInclude = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    public bool Paused { get; set; }

    /// <summary>Raised on the UI thread; the bool is true when a new row was created.</summary>
    public event Action<ClipItem, bool>? ItemCaptured;

    public ClipboardMonitor(Storage storage, AppSettings settings)
    {
        _storage = storage;
        _settings = settings;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await CaptureAsync(); };

        // a message-only window, created hidden and non-activatable: HwndSourceParameters defaults to WS_VISIBLE,
        // and a visible one took the foreground when ClipNest started, leaving the user's typing nowhere
        var p = new HwndSourceParameters("ClipNestClipboardListener")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
            ExtendedWindowStyle = (int)(Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW),
            Width = 0,
            Height = 0,
        };
        _hwnd = new HwndSource(p);
        _hwnd.AddHook(WndProc);
        if (!Native.AddClipboardFormatListener(_hwnd.Handle))
            Log.Write("AddClipboardFormatListener failed: " + Marshal.GetLastWin32Error());
    }

    /// <summary>Call right after ClipNest itself writes to the clipboard.</summary>
    public void IgnoreOwnChange()
    {
        _ignoreSeq = Native.GetClipboardSequenceNumber();
        _ignoreUntil = Environment.TickCount64 + 400;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private async Task CaptureAsync()
    {
        if (Paused) return;
        if (_busy)
        {
            // a copy arrived while the previous one (e.g. a big image) is still being processed: take it afterwards
            _pending = true;
            return;
        }
        var seq = Native.GetClipboardSequenceNumber();
        if (seq == _ignoreSeq || Environment.TickCount64 < _ignoreUntil) return;

        // content that password managers flag as "keep out of history": skipped, or kept as a secret if the user allows it
        bool flagged = Native.IsClipboardFormatAvailable(FmtExclude) || Native.IsClipboardFormatAvailable(FmtViewerIgnore);
        if (flagged && _settings.RespectPrivacyFlags) return;

        var (exe, source) = OwnerProcess();
        if (exe is not null && _settings.ExcludedApps.Any(a => string.Equals(a.Trim(), exe, StringComparison.OrdinalIgnoreCase)))
            return;

        _busy = true;
        try
        {
            IDataObject? data = null;
            for (int attempt = 0; attempt < 5 && data is null; attempt++)
            {
                try { data = Clipboard.GetDataObject(); }
                catch (COMException) { await Task.Delay(50); }
            }
            if (data is null) return;

            if (Native.IsClipboardFormatAvailable(FmtCanInclude)
                && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var buf = new byte[4];
                ms.Read(buf, 0, 4);
                if (BitConverter.ToInt32(buf, 0) == 0)
                {
                    if (_settings.RespectPrivacyFlags) return;
                    flagged = true;
                }
            }

            var item = ReadFiles(data) ?? ReadText(data, flagged) ?? (_settings.SaveImages ? await ReadImageAsync(data, flagged) : null);
            if (item is null) return;
            item.Source = source;

            bool isNew = _storage.Add(item);
            if (isNew && _settings.MaxItems > 0) _storage.Prune(_settings.MaxItems, 0);
            ItemCaptured?.Invoke(item, isNew);
        }
        catch (Exception ex)
        {
            Log.Write("capture: " + ex);
        }
        finally
        {
            _busy = false;
            if (_pending)
            {
                _pending = false;
                _debounce.Start();
            }
        }
    }

    private static (string? exe, string? source) OwnerProcess()
    {
        try
        {
            var owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) owner = Native.GetForegroundWindow();
            if (owner == IntPtr.Zero) return (null, null);
            Native.GetWindowThreadProcessId(owner, out var pid);
            if (pid == Environment.ProcessId) return (null, null);
            using var p = Process.GetProcessById((int)pid);
            var exe = p.ProcessName + ".exe";
            // MainModule walks the module list (slow, and throws for elevated apps): look it up once per program
            if (!SourceNames.TryGetValue(exe, out var name))
            {
                string? desc = null;
                try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                name = string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim();
                SourceNames[exe] = name;
            }
            return (exe, name);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string Sha(string prefix, byte[] data) => Secrets.PlainHash(prefix, data);
    private static string Sha(string prefix, string s) => Secrets.PlainHash(prefix, s);

    private static ClipItem? ReadFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;
        var joined = string.Join("\n", files);
        return new ClipItem
        {
            Kind = ClipKind.Files,
            Files = joined,
            Hash = Sha("f:", joined),
            Size = files.Length,
        };
    }

    private ClipItem? ReadText(IDataObject data, bool flagged)
    {
        if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
        if (data.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text)) return null;

        string? html = null, rtf = null;
        try { if (data.GetDataPresent(DataFormats.Html)) html = data.GetData(DataFormats.Html) as string; } catch { }
        try { if (data.GetDataPresent(DataFormats.Rtf)) rtf = data.GetData(DataFormats.Rtf) as string; } catch { }
        if (html?.Length > MaxRichFormatChars) html = null;
        if (rtf?.Length > MaxRichFormatChars) rtf = null;

        var trimmed = text.Trim();
        bool isLink = !trimmed.Contains('\n') && !trimmed.Contains(' ')
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "ftp");

        // an existing row decides first: the user may have marked or unmarked this exact text before
        var secretHash = Secrets.Hash(text);
        var plainHash = Sha("t:", text);
        bool secret = _storage.HashExists(secretHash) || (!_storage.HashExists(plainHash)
            && (flagged || (_settings.HideSecrets && SecretDetector.IsSecret(text))));

        return new ClipItem
        {
            Kind = isLink && !secret ? ClipKind.Link : ClipKind.Text,
            Text = text,
            Html = string.IsNullOrEmpty(html) || secret ? null : html,
            Rtf = string.IsNullOrEmpty(rtf) || secret ? null : rtf,
            Hash = secret ? secretHash : plainHash,
            Secret = secret,
            Size = text.Length * 2L,
        };
    }

    private async Task<ClipItem?> ReadImageAsync(IDataObject data, bool flagged)
    {
        byte[]? png = null;
        int w = 0, h = 0;

        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
        {
            png = pngStream.ToArray();
            try
            {
                var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                w = frame.PixelWidth; h = frame.PixelHeight;
            }
            catch { png = null; }
        }

        byte[]? pixels = null;
        int stride = 0;
        if (png is null)
        {
            if (!Clipboard.ContainsImage()) return null;
            BitmapSource? src = null;
            try { src = Clipboard.GetImage(); } catch { }
            if (src is null) return null;

            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            w = bgra.PixelWidth; h = bgra.PixelHeight;
            stride = w * 4;
            pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            // 32-bit DIBs from many apps (Print Screen included) carry an all-zero alpha channel
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        if (w <= 0 || h <= 0) return null;

        var dpx = pixels; var st = stride; var ww = w; var hh = h;
        var (bytes, hash) = await Task.Run(() =>
        {
            var b = png;
            if (b is null)
            {
                var bmp = BitmapSource.Create(ww, hh, 96, 96, PixelFormats.Bgra32, null, dpx!, st);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var outMs = new MemoryStream();
                enc.Save(outMs);
                b = outMs.ToArray();
            }
            return (b, Sha("i:", dpx ?? b));
        });

        if (_settings.MaxImageMb > 0 && bytes.Length > _settings.MaxImageMb * 1024L * 1024) return null;

        var item = new ClipItem { Kind = ClipKind.Image, Width = w, Height = h, Hash = hash, Size = bytes.Length };
        if (_storage.HashExists(hash))
        {
            // a known image: the stored row keeps its file, secrecy and regions; Add only moves it to the top
            item.ImagePath = Path.Combine(_storage.ImagesDir, hash[2..] + Secrets.EncryptedImageExt);
            return item;
        }

        // a new image is checked for secrets before anything reaches the disk
        if (flagged)
        {
            item.Secret = true; // from a password manager, kept because privacy flags are off: hide it whole
        }
        else if (_settings.ScanImages && ImageScanner.Available)
        {
            var scan = await Task.Run(async () =>
                await ImageScanner.ScanAsync(dpx ?? DecodeBgra(bytes), ww, hh));
            if (scan is not null)
            {
                if (scan.Milliseconds > 1000) Log.Write($"OCR took {scan.Milliseconds:0} ms for {ww}x{hh}");
                if (scan.Secrets.Count > 0 || scan.WholeImage)
                {
                    item.Secret = true;
                    item.Regions = scan.WholeImage ? null : ImageScanner.Serialize(scan.Secrets);
                }
                else
                {
                    item.OcrText = scan.Text; // searchable by the text on it
                }
            }
        }

        // every image is stored encrypted (DPAPI, this Windows account), secret or not
        var file = Path.Combine(_storage.ImagesDir, hash[2..] + Secrets.EncryptedImageExt);
        await File.WriteAllBytesAsync(file, Secrets.Protect(bytes));
        item.ImagePath = file;
        return item;
    }

    /// <summary>Bgra32 pixels of a PNG, for OCR. Worker thread.</summary>
    internal static byte[] DecodeBgra(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource src = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * 4 * src.PixelHeight];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return px;
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_hwnd.Handle);
        _hwnd.Dispose();
    }
}
