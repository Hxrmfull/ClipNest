using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClipNest.Services;

/// <summary>A newer release: its version and the files of this edition.</summary>
public sealed record UpdateInfo(Version Version, Uri ExeUrl, Uri SignatureUrl, long Size, string? Sha256);

/// <summary>A failed check or update, with a reason fit for the settings window.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Updates from the GitHub releases, only when asked (the button in the settings: nothing is checked on its own).
/// The exe of this edition is downloaded next to the running one, checked against its signature, then put in its place:
/// a running exe can be renamed, so the old one steps aside as ClipNest.exe.old and goes on the next start.
/// Every release exe is signed with the project's key (tools/release.ps1); a file without a valid signature is refused,
/// whatever server it came from.</summary>
public static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/Hxrmfull/ClipNest/releases/latest";
    /// <summary>The public half of the release signing key (ECDSA P-256, SubjectPublicKeyInfo).</summary>
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEC1DqGg8qsBeCA6X0W4G0Qqfxg4V33yutmi6XnlMkm8TtarHJ/gHn9IhwshFBrYo3OQbyA1ftpQkOqMWNVJmRTA==";

#if OCR
    public const string AssetName = "ClipNest-OCR.exe";
#else
    public const string AssetName = "ClipNest.exe";
#endif

    public static Version Current { get; } = Trim(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>Where the release is asked for; tests point it at a local server (the signature check stays).</summary>
    private static string Source => Environment.GetEnvironmentVariable("CLIPNEST_UPDATE_URL") is { Length: > 0 } url ? url : LatestRelease;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipNest/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The newer release, or null when this one is the latest.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            using var response = await Http.GetAsync(Source, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException(Loc.T("UpdErrNoRelease"));
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException(Loc.T("UpdErrNetwork"), ex);
        }
        return Parse(json, Current, AssetName);
    }

    /// <summary>The release JSON of the GitHub API: the version of tag_name ("v1.2.0") and the assets of this edition.</summary>
    public static UpdateInfo? Parse(string json, Version current, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new UpdateException(Loc.T("UpdErrNoRelease"));
        version = Trim(version);
        if (version <= current) return null;
        JsonElement? exe = null, sig = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) exe = a;
            else if (string.Equals(name, assetName + ".sig", StringComparison.OrdinalIgnoreCase)) sig = a;
        }
        if (exe is not { } e || sig is not { } s) throw new UpdateException(Loc.T("UpdErrNoAsset"));
        string? sha = e.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:", StringComparison.Ordinal)
            ? dg["sha256:".Length..] : null;
        return new UpdateInfo(version, new Uri(e.GetProperty("browser_download_url").GetString()!),
            new Uri(s.GetProperty("browser_download_url").GetString()!), e.GetProperty("size").GetInt64(), sha);
    }

    /// <summary>Downloads and checks the new exe and puts it in place of the running one; the caller then restarts.
    /// On any failure the running exe is left as it was.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new UpdateException(Loc.T("UpdErrNoAccess"));
        var download = exe + ".download";
        try
        {
            try
            {
                using var response = await Http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? update.Size;
                await using (var from = await response.Content.ReadAsStreamAsync(ct))
                await using (var to = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await from.ReadAsync(buffer, ct)) > 0)
                    {
                        await to.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                    }
                }
                var signature = (await Http.GetStringAsync(update.SignatureUrl, ct)).Trim();
                Verify(download, update, signature);
            }
            catch (UnauthorizedAccessException ex) { throw new UpdateException(Loc.T("UpdErrNoAccess"), ex); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new UpdateException(Loc.T("UpdErrNetwork"), ex);
            }
            Swap(exe, download);
        }
        finally
        {
            try { File.Delete(download); } catch { }
        }
    }

    /// <summary>The size, the SHA-256 the release states, and the signature made with the project's key.</summary>
    public static void Verify(string file, UpdateInfo update, string signatureBase64)
    {
        var bytes = File.ReadAllBytes(file);
        var hash = SHA256.HashData(bytes);
        if (bytes.Length != update.Size
            || (update.Sha256 is { } sha && !string.Equals(Convert.ToHexString(hash), sha, StringComparison.OrdinalIgnoreCase))
            || !SignatureValid(hash, signatureBase64, PublicKey))
            throw new UpdateException(Loc.T("UpdErrSignature"));
    }

    public static bool SignatureValid(byte[] hash, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyHash(hash, Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>The new exe in the place of the running one, which steps aside as .old (deleted on the next start).</summary>
    public static void Swap(string exe, string replacement)
    {
        var old = exe + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UpdateException(Loc.T("UpdErrNoAccess"), ex);
        }
        try
        {
            File.Move(replacement, exe);
        }
        catch (Exception ex)
        {
            try { File.Move(old, exe); } catch { } // put the running one back
            throw new UpdateException(Loc.T("UpdErrNoAccess"), ex);
        }
    }

    /// <summary>On start: the exe an update set aside, once the old process has let go of it.</summary>
    public static async void CleanUp()
    {
        if (Environment.ProcessPath is not { } exe) return;
        foreach (var leftover in new[] { exe + ".old", exe + ".download" })
            for (int attempt = 0; attempt < 20 && File.Exists(leftover); attempt++)
            {
                try { File.Delete(leftover); }
                catch { await Task.Delay(500); }
            }
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
