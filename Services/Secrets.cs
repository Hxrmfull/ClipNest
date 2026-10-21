using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClipNest.Services;

/// <summary>Encryption at rest for secret items (Windows DPAPI, bound to the current user)
/// plus a keyed hash so duplicates can be found without storing a plain hash of a password.</summary>
public static class Secrets
{
    public const string TextPrefix = "enc:";
    public const string EncryptedImageExt = ".enc";
    private static byte[] _hmacKey = Array.Empty<byte>();

    public static void Init(string dataDir)
    {
        var path = Path.Combine(dataDir, "secret.key");
        try
        {
            if (File.Exists(path))
            {
                _hmacKey = Unprotect(File.ReadAllBytes(path));
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("secret.key unreadable, creating a new one: " + ex.Message);
        }
        _hmacKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, Protect(_hmacKey));
    }

    /// <summary>The SQLCipher key of history.db as a raw-key literal (x'…': no slow key derivation on open).
    /// Derived from the DPAPI-protected secret.key, so only this Windows account can open the database.</summary>
    public static string DatabaseKeyPragma =>
        "x'" + Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes("clipnest/history.db/v1"))) + "'";

    /// <summary>Content hash for ordinary items ("t:" text, "f:" files, "i:" images).</summary>
    public static string PlainHash(string prefix, byte[] data) => prefix + Convert.ToHexString(SHA256.HashData(data))[..32];
    public static string PlainHash(string prefix, string s) => PlainHash(prefix, Encoding.UTF8.GetBytes(s));

    /// <summary>Keyed hash for secret text: lets duplicates collapse without a brute-forceable hash of the password.</summary>
    public static string Hash(string text) =>
        "s:" + Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(text)))[..32];

    public static string EncryptText(string text) => TextPrefix + Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(text)));

    public static string? DecryptText(string? stored)
    {
        if (stored is null || !stored.StartsWith(TextPrefix, StringComparison.Ordinal)) return stored;
        try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(stored[TextPrefix.Length..]))); }
        catch { return Loc.T("DecryptFailed"); }
    }

    /// <summary>Reads an image file, decrypting it if it is stored encrypted.</summary>
    public static byte[] ReadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return path.EndsWith(EncryptedImageExt, StringComparison.OrdinalIgnoreCase) ? Unprotect(bytes) : bytes;
    }

    public static byte[] Protect(byte[] data) => Crypt(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
