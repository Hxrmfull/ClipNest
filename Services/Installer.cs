using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ClipNest.Services;

/// <summary>ClipNest has no installer: the exe runs from wherever it was saved. Started from anywhere else, it offers
/// once to move itself to %LOCALAPPDATA%\Programs\ClipNest (the per-user program folder: no admin rights, writable for
/// updates) with a Start menu shortcut, and restarts from there. The data folder stays where it is; autostart follows
/// on its own (<see cref="Autostart.Set"/> writes the path of the exe that runs).</summary>
public static class Installer
{
    public const string ExeName = "ClipNest.exe";

    /// <summary>The program folder. CLIPNEST_INSTALL_DIR puts it elsewhere, for tests.</summary>
    public static string Dir =>
        Environment.GetEnvironmentVariable("CLIPNEST_INSTALL_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClipNest");

    public static string Target => Path.Combine(Dir, ExeName);

    /// <summary>The Start menu shortcut. CLIPNEST_SHORTCUT_DIR puts it elsewhere, for tests.</summary>
    public static string Shortcut => Path.Combine(
        Environment.GetEnvironmentVariable("CLIPNEST_SHORTCUT_DIR") is { Length: > 0 } dir ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "ClipNest.lnk");

    /// <summary>Whether to ask about moving <paramref name="exe"/>. Not for a portable copy (its data sits beside it),
    /// not from the program folder itself, not for a file the user chose to keep where it is, and not over a newer
    /// installed version (that would be a downgrade).</summary>
    public static bool ShouldOffer(string exe, string installDir, string? declined, bool portable, Version? installed, Version current)
    {
        if (portable) return false;
        if (Path.GetDirectoryName(exe) is not { } dir || DataLocation.SamePath(dir, installDir)) return false;
        if (declined is not null && DataLocation.SamePath(declined, exe)) return false;
        return installed is null || installed <= current;
    }

    /// <summary>The version of the copy already in the program folder, if there is one.</summary>
    public static Version? InstalledVersion()
    {
        try
        {
            if (!File.Exists(Target)) return null;
            var v = FileVersionInfo.GetVersionInfo(Target);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch { return null; }
    }

    /// <summary>Copies <paramref name="exe"/> into the program folder (over an older copy there) and makes the Start menu
    /// shortcut. A failed copy leaves nothing behind and throws; a failed shortcut is only logged.</summary>
    public static string Install(string exe, string description)
    {
        var target = Target;
        Directory.CreateDirectory(Dir);
        var part = target + ".download"; // the same leftover name as an update's, so Updater.CleanUp clears it too
        try
        {
            // the bytes only: File.Copy would carry the download's "from the internet" mark (Zone.Identifier) over to
            // the installed copy, which a file written by an installer doesn't have
            using (var src = File.OpenRead(exe))
            using (var dst = File.Create(part))
                src.CopyTo(dst);
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        try { CreateShortcut(Shortcut, target, description); }
        catch (Exception ex) { Log.Write("start menu shortcut: " + ex.Message); }
        return target;
    }

    /// <summary>Run by the installed copy (--installed &lt;old exe&gt;): deletes the file it was moved from once that
    /// process has exited, with what an update left next to it. Only a ClipNest exe, and never the running one.</summary>
    public static async void RemoveOld(string old)
    {
        try
        {
            if (Environment.ProcessPath is { } me && DataLocation.SamePath(me, old)) return;
            if (!File.Exists(old) || FileVersionInfo.GetVersionInfo(old).ProductName != "ClipNest") return;
        }
        catch { return; }
        foreach (var file in new[] { old, old + ".old", old + ".download" })
            for (int attempt = 0; attempt < 20 && File.Exists(file); attempt++)
            {
                try { File.Delete(file); }
                catch { await Task.Delay(500); }
            }
        Log.Write(File.Exists(old) ? "installed; couldn't remove " + old : "installed; removed " + old);
    }

    public static void CreateShortcut(string lnk, string target, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Where a shortcut points (for the tests).</summary>
    public static string ShortcutTarget(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
            var path = new StringBuilder(260);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
