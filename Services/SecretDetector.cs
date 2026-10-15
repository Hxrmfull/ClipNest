using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClipNest.Services;

/// <summary>Heuristics for "this looks like a password, token, key or card number".
/// A false positive only blurs the item, so the rules lean towards hiding.</summary>
public static partial class SecretDetector
{
    public static bool IsSecret(string text)
    {
        var t = text.Trim();
        if (t.Length < 6) return false;

        if (t.Contains("-----BEGIN") && t.Contains("PRIVATE KEY")) return true;
        if (t.Length <= 400 && LabeledSecret().IsMatch(t)) return true;
        if (IsCardNumber(t)) return true;

        if (t.Length > 512 || t.Any(char.IsWhiteSpace)) return false;
        if (KnownToken().IsMatch(t)) return true;
        return LooksLikePassword(t);
    }

    private static bool LooksLikePassword(string t)
    {
        if (t.Length is < 8 or > 64) return false;
        if (t.Contains("://") || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Contains('\\') || t.StartsWith('/') || t.StartsWith('#')) return false;
        if (Email().IsMatch(t) || Version().IsMatch(t) || Guid.TryParse(t, out _)) return false;

        bool lower = t.Any(char.IsLower), upper = t.Any(char.IsUpper), digit = t.Any(char.IsDigit);
        bool letter = lower || upper;
        // "._-" are common in identifiers and file names, so they don't count as password symbols
        bool special = t.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-'));
        if (!(digit && letter && (special || (lower && upper)))) return false;

        return Entropy(t) >= 2.9;
    }

    private static double Entropy(string s)
    {
        return s.GroupBy(c => c)
            .Select(g => (double)g.Count() / s.Length)
            .Sum(p => -p * Math.Log2(p));
    }

    private static bool IsCardNumber(string t)
    {
        if (t.Length > 23 || !CardShape().IsMatch(t)) return false;
        var digits = t.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"^(sk-[A-Za-z0-9_\-]{20,}|sk-ant-[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_\w{40,}|glpat-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|\d{8,10}:[A-Za-z0-9_\-]{35}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}|npm_[A-Za-z0-9]{36}|pypi-[A-Za-z0-9_\-]{40,}|(sk|rk|pk)_(live|test)_[A-Za-z0-9]{16,}|hf_[A-Za-z0-9]{30,})$")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?i)(парол[ьяи]|password|passwd|pass|pwd|secret|секрет|token|токен|api[_\- ]?key|ключ|pin|пин)\s*[:=]\s*\S{3,}")]
    private static partial Regex LabeledSecret();

    [GeneratedRegex(@"^\d[\d \-]{11,21}\d$")]
    private static partial Regex CardShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^v?\d+(\.\d+)+([\-+][\w.\-]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
}
