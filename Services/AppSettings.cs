using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClipNest.Services;

public enum ThemeMode { System, Dark, Light }

public sealed class AppSettings
{
    /// <summary>Intercept Win+V and open ClipNest instead of the built-in panel.</summary>
    public bool ReplaceWinV { get; set; } = true;
    public bool Autostart { get; set; } = true;
    /// <summary>A pasted item jumps to the top of the history.</summary>
    public bool MoveUsedToTop { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>0 = unlimited.</summary>
    public int MaxItems { get; set; } = 0;
    /// <summary>0 = keep forever.</summary>
    public int MaxAgeDays { get; set; } = 0;
    public bool SaveImages { get; set; } = true;
    /// <summary>Largest image to keep, in MB of PNG. 0 = unlimited.</summary>
    public int MaxImageMb { get; set; } = 0;

    /// <summary>Respect the "don't put this in history" flags that password managers set.</summary>
    public bool RespectPrivacyFlags { get; set; } = true;
    /// <summary>Auto-detect passwords, tokens, keys and card numbers; they are blurred and encrypted.</summary>
    public bool HideSecrets { get; set; } = true;
    public List<string> ExcludedApps { get; set; } = new()
    {
        "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "LastPass.exe", "Enpass.exe",
    };

    public bool ShownWelcome { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        AppSettings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch
        {
            s = new();
        }
        s.FilePath = path;
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
    }
}

public static class Log
{
    public static string? FilePath { get; set; }

    public static void Write(string msg)
    {
        if (FilePath is null) return;
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
