using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ClipNest.Services;

/// <summary>Where the history lives. By default Documents\ClipNest; the user can pick another folder,
/// remembered in the registry (settings.json itself lives in the data folder, so it can't hold its own location).
/// portable.txt next to the exe still wins: data\ beside the program.</summary>
public static class DataLocation
{
    private const string Key = @"Software\ClipNest";
    private const string Value = "DataDir";

    /// <summary>Everything ClipNest keeps in its folder; only these are moved, so a shared folder is safe to pick.</summary>
    private static readonly string[] Files =
        { "history.db", "history.db-wal", "history.db-shm", "settings.json", "secret.key", "clipnest.log" };
    private const string ImagesDir = "images";

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClipNest");

    /// <summary>Where versions before 1.1 kept the data.</summary>
    public static string LegacyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipNest");

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    public static string? CustomDir
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Value) as string is { Length: > 0 } s ? s : null;
            }
            catch { return null; }
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (string.IsNullOrEmpty(value) || SamePath(value, DefaultDir)) k.DeleteValue(Value, false);
            else k.SetValue(Value, value);
        }
    }

    /// <summary>Picks the data folder at startup; moves the data out of the old %LOCALAPPDATA% location once.</summary>
    public static string Resolve()
    {
        if (IsPortable) return Path.Combine(AppContext.BaseDirectory, "data");
        var dir = CustomDir ?? DefaultDir;
        if (!HasHistory(dir) && HasHistory(LegacyDir) && !SamePath(dir, LegacyDir))
        {
            try
            {
                Move(LegacyDir, dir);
            }
            catch (Exception ex)
            {
                // keep working from the old place rather than start with an empty history
                Log.Write("moving data from " + LegacyDir + " failed: " + ex.Message);
                return LegacyDir;
            }
        }
        return dir;
    }

    public static bool HasHistory(string dir) => File.Exists(Path.Combine(dir, "history.db"));

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies ClipNest's files from one folder to another, then deletes the originals.
    /// If copying fails, the copies are removed and the originals stay untouched. The database must be closed.</summary>
    public static void Move(string from, string to)
    {
        Directory.CreateDirectory(to);
        var copied = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var f in Files)
            {
                var src = Path.Combine(from, f);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(to, f);
                File.Copy(src, dst, true);
                copied.Add(dst);
            }
            var imgFrom = Path.Combine(from, ImagesDir);
            if (Directory.Exists(imgFrom))
            {
                var imgTo = Path.Combine(to, ImagesDir);
                Directory.CreateDirectory(imgTo);
                foreach (var src in Directory.EnumerateFiles(imgFrom))
                {
                    var dst = Path.Combine(imgTo, Path.GetFileName(src));
                    File.Copy(src, dst, true);
                    copied.Add(dst);
                }
            }
        }
        catch
        {
            foreach (var c in copied) { try { File.Delete(c); } catch { } }
            throw;
        }

        // delete exactly what was copied; folders only when nothing else is left in them
        foreach (var c in copied)
        {
            var rel = Path.GetRelativePath(to, c);
            try { File.Delete(Path.Combine(from, rel)); } catch { }
        }
        DeleteIfEmpty(Path.Combine(from, ImagesDir));
        DeleteIfEmpty(from);
        Log.Write($"data moved from {from} to {to}");
    }

    private static void DeleteIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    /// <summary>The folder the data will actually go to when the user picks <paramref name="picked"/>:
    /// an existing ClipNest folder or an empty one is used as is; any other folder gets a ClipNest subfolder,
    /// so the history doesn't mix with the user's own files.</summary>
    public static string TargetFor(string picked)
    {
        if (HasHistory(picked)) return picked;
        if (Path.GetFileName(picked.TrimEnd('\\', '/')).Equals("ClipNest", StringComparison.OrdinalIgnoreCase)) return picked;
        try
        {
            if (!Directory.Exists(picked) || !Directory.EnumerateFileSystemEntries(picked).Any()) return picked;
        }
        catch { }
        return Path.Combine(picked, "ClipNest");
    }
}
