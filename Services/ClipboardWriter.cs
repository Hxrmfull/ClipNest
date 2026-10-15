using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipNest.Models;

namespace ClipNest.Services;

public static class ClipboardWriter
{
    /// <summary>Puts the item on the clipboard. With plain=true only text goes, without HTML/RTF.
    /// overrideText replaces the text (used by the transforms).</summary>
    public static bool Set(ClipItem item, bool plain, string? overrideText = null)
    {
        var d = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Image:
                if (item.ImagePath is null || !File.Exists(item.ImagePath)) return false;
                var bytes = Secrets.ReadImage(item.ImagePath);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                d.SetImage(bmp);
                d.SetData("PNG", new MemoryStream(bytes));
                break;

            case ClipKind.Files:
                var files = item.FileList;
                if (plain)
                {
                    d.SetText(string.Join(Environment.NewLine, files), TextDataFormat.UnicodeText);
                }
                else
                {
                    var sc = new StringCollection();
                    sc.AddRange(files);
                    d.SetFileDropList(sc);
                    d.SetText(string.Join(Environment.NewLine, files), TextDataFormat.UnicodeText);
                }
                break;

            default:
                d.SetText(overrideText ?? item.Text ?? "", TextDataFormat.UnicodeText);
                if (!plain && overrideText is null)
                {
                    if (!string.IsNullOrEmpty(item.Html)) d.SetData(DataFormats.Html, item.Html);
                    if (!string.IsNullOrEmpty(item.Rtf)) d.SetData(DataFormats.Rtf, item.Rtf);
                }
                break;
        }

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
