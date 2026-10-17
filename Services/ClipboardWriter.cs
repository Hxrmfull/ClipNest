using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Buffers.Binary;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;

namespace ClipNest.Services;

public static class ClipboardWriter
{
    /// <summary>Puts the item on the clipboard. With plain=true only text goes, without HTML/RTF.
    /// overrideText replaces the text (used by the transforms). Must run on the UI (STA) thread;
    /// for images the heavy part (decoding the PNG, building the bitmap) runs on a worker meanwhile.</summary>
    public static async Task<bool> SetAsync(ClipItem item, bool plain, string? overrideText = null)
    {
        var d = await BuildAsync(item, plain, overrideText);
        return d is not null && Put(d);
    }

    public static Task<DataObject?> BuildAsync(ClipItem item, bool plain, string? overrideText) =>
        item.Kind == ClipKind.Image
            ? Task.Run(() => BuildImage(item))
            : Task.FromResult<DataObject?>(BuildOther(item, plain, overrideText));

    /// <summary>PNG for apps that take it (keeps transparency) plus a ready CF_DIB. Windows synthesizes CF_BITMAP and
    /// CF_DIBV5 from the DIB itself. Handing WPF a BitmapSource instead made it convert on the UI thread:
    /// ~250 ms for a 4K screenshot before anything was pasted.</summary>
    private static DataObject? BuildImage(ClipItem item)
    {
        if (item.ImagePath is null || !File.Exists(item.ImagePath)) return null;
        try
        {
            var png = Secrets.ReadImage(item.ImagePath);
            var d = new DataObject();
            d.SetData(DataFormats.Dib, new MemoryStream(ToDib(png)), false);
            d.SetData("PNG", new MemoryStream(png), false);
            return d;
        }
        catch (Exception ex)
        {
            Log.Write("image to clipboard: " + ex.Message);
            return null;
        }
    }

    /// <summary>32-bit bottom-up DIB: BITMAPINFOHEADER followed by the pixels.</summary>
    private static byte[] ToDib(byte[] png)
    {
        var dec = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);

        const int header = 40;
        var dib = new byte[header + pixels.Length];
        var span = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[0..], header);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], h);      // positive height: bottom-up rows
        BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);     // planes
        BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);    // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 0);     // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], 3780);  // 96 dpi in pixels per metre
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], 3780);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(pixels, y * stride, dib, header + (h - 1 - y) * stride, stride);
        return dib;
    }

    private static DataObject BuildOther(ClipItem item, bool plain, string? overrideText)
    {
        var d = new DataObject();
        if (item.Kind == ClipKind.Files)
        {
            var files = item.FileList;
            if (!plain)
            {
                var sc = new StringCollection();
                sc.AddRange(files);
                d.SetFileDropList(sc);
            }
            d.SetText(string.Join(Environment.NewLine, files), TextDataFormat.UnicodeText);
            return d;
        }

        d.SetText(overrideText ?? item.Text ?? "", TextDataFormat.UnicodeText);
        if (!plain && overrideText is null)
        {
            if (!string.IsNullOrEmpty(item.Html)) d.SetData(DataFormats.Html, item.Html);
            if (!string.IsNullOrEmpty(item.Rtf)) d.SetData(DataFormats.Rtf, item.Rtf);
        }
        return d;
    }

    /// <summary>Places a built data object on the clipboard, retrying while another program holds it open.</summary>
    public static bool Put(DataObject d)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(d, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }
}

public static class InputSender
{
    private static Native.INPUT Key(int vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = (ushort)vk,
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = Native.InjectedMarker,
            },
        },
    };

    private static void Send(params Native.INPUT[] inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Releases modifiers the user may still be holding (Shift from Shift+Enter, etc.), then sends Ctrl+V.</summary>
    public static void SendPaste()
    {
        var list = new System.Collections.Generic.List<Native.INPUT>();
        foreach (var vk in new[] { Native.VK_LSHIFT, Native.VK_RSHIFT, Native.VK_LMENU, Native.VK_RMENU, Native.VK_LWIN, Native.VK_RWIN })
            if (IsDown(vk)) list.Add(Key(vk, true));
        if (IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN))
        {
            list.Insert(0, Key(Native.VK_MASK, false));
            list.Insert(1, Key(Native.VK_MASK, true));
        }
        list.Add(Key(Native.VK_CONTROL, false));
        list.Add(Key(Native.VK_V, false));
        list.Add(Key(Native.VK_V, true));
        list.Add(Key(Native.VK_CONTROL, true));
        Send(list.ToArray());
    }

    /// <summary>A no-op keystroke: keeps Start from opening on Win release, and gives us the "last input" so we may take focus.</summary>
    public static void SendMaskKey() => Send(Key(Native.VK_MASK, false), Key(Native.VK_MASK, true));
}
