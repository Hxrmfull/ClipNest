using System;
using System.Collections.Generic;
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

    /// <summary>For text recognized on an image: which runs of words are secrets (first and last word index).
    /// Stricter than <see cref="IsSecret"/>: OCR misreads characters and screenshots are full of identifiers,
    /// so a bare "password-like" word doesn't count, only shapes that don't occur by accident:
    /// known token formats, card numbers that pass the Luhn check, and values after a label like «Пароль:».
    /// A private key block makes the whole image secret.</summary>
    public static List<(int First, int Last)> FindInWords(IReadOnlyList<string> words, out bool wholeImage)
    {
        var found = new List<(int, int)>();
        var line = string.Join(' ', words);
        wholeImage = line.Contains("BEGIN") && line.Contains("PRIVATE KEY");

        for (int i = 0; i < words.Count; i++)
        {
            var w = TrimPunct(words[i]);
            // a long token often comes out of OCR cut into 2–3 "words" («…TcvCH | 1 | vGWJ…»), with stray quote
            // marks («…uwv'xyzAB») and a character lost or gained: glue neighbours and use length-tolerant formats
            int tokenEnd = -1;
            if (OcrTokenStart().IsMatch(w))
            {
                var glued = new System.Text.StringBuilder();
                for (int k = i; k < words.Count && k < i + 3; k++)
                {
                    var part = OcrNoise().Replace(TrimPunct(words[k]), "");
                    if (k > i && !TokenPart().IsMatch(part)) break;
                    glued.Append(part);
                    if (glued.Length >= 10 && OcrToken().IsMatch(glued.ToString())) tokenEnd = k;
                }
            }
            if (tokenEnd >= 0)
            {
                found.Add((i, tokenEnd));
                i = tokenEnd;
                continue;
            }

            // «Пароль: hunter2», «token=abc…», «PIN: 1234»
            var m = LabelWord().Match(w);
            if (!m.Success) continue;
            var rest = m.Groups["rest"].Value;
            bool hasSep = m.Groups["sep"].Success;
            if (hasSep && rest.Length >= 3)
            {
                found.Add((i, i)); // label and value glued together
                continue;
            }
            int j = i + 1;
            if (!hasSep)
            {
                // the separator came out as its own word, or there is none: then it isn't a label
                if (j >= words.Count || words[j].Trim() is not (":" or "=")) continue;
                j++;
            }
            if (j < words.Count && TrimPunct(words[j]).Length >= 3)
            {
                found.Add((j, j));
                i = j;
            }
        }

        // card numbers: up to 5 neighbouring digit groups («4276 3801 2345 6787», «4276-3801-…»)
        for (int i = 0; i < words.Count; i++)
        {
            if (!IsDigitGroup(words[i])) continue;
            // after «карта»/«card» a misread digit (OCR does that) must not let the number through the Luhn check
            bool labeled = i > 0 && CardWord().IsMatch(TrimPunct(words[i - 1]).TrimEnd(':'));
            var digits = new System.Text.StringBuilder();
            for (int j = i; j < words.Count && j < i + 5 && IsDigitGroup(words[j]); j++)
            {
                foreach (var c in words[j]) if (char.IsDigit(c)) digits.Append(c);
                if (digits.Length > 19) break;
                // a single 13-digit word is more often an id or a timestamp than a card
                bool shape = digits.Length >= 13 && (j > i || digits.Length >= 15);
                bool more = j + 1 < words.Count && j + 1 < i + 5 && IsDigitGroup(words[j + 1]);
                if ((shape && Luhn(digits.ToString())) || (labeled && digits.Length >= 12 && !more))
                {
                    // a last group OCR couldn't read as digits («4444» → «аааа») is still on the picture: cover it too
                    if (labeled && j + 1 < words.Count && TrimPunct(words[j + 1]).Length is > 0 and <= 4
                        && digits.Length < 16) j++;
                    found.Add((i, j));
                    i = j;
                    break;
                }
            }
        }
        return found;
    }

    private static string TrimPunct(string w) => w.Trim().Trim('"', '\'', '«', '»', '(', ')', '[', ']', ',', ';', '.');

    private static bool IsDigitGroup(string w)
    {
        int digits = 0;
        foreach (var c in w)
        {
            if (char.IsDigit(c)) digits++;
            else if (c != '-') return false;
        }
        return digits >= 2;
    }

    private static bool Luhn(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
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

    [GeneratedRegex(@"(?i)^(парол[ьяи]|password|passwd|pass|pwd|secret|секрет|token|токен|api[_\-]?key|apikey|ключ|pin|пин|пин-код)(?<sep>\s*[:=])?(?<rest>.*)$")]
    private static partial Regex LabelWord();

    [GeneratedRegex(@"['`’‘""]")]
    private static partial Regex OcrNoise();

    /// <summary>The same token formats as <see cref="KnownToken"/>, with room for a character OCR lost or added.</summary>
    [GeneratedRegex(@"^(sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{26,}|github_pat_\w{32,}|glpat-[A-Za-z0-9_\-]{16,}|xox[abprs]-[A-Za-z0-9\-]{8,}|AKIA[0-9A-Z]{14,18}|AIza[0-9A-Za-z_\-]{31,39}|\d{8,10}:[A-Za-z0-9_\-]{30,40}|eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}|npm_[A-Za-z0-9]{32,40}|pypi-[A-Za-z0-9_\-]{32,}|(sk|rk|pk)_(live|test)_[A-Za-z0-9]{12,}|hf_[A-Za-z0-9]{26,})$")]
    private static partial Regex OcrToken();

    [GeneratedRegex(@"^(sk-|gh[pousr]_|github_pat_|glpat-|xox[abprs]-|AKIA|AIza|\d{8,10}:|eyJ|npm_|pypi-|(sk|rk|pk)_(live|test)_|hf_)")]
    private static partial Regex OcrTokenStart();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex TokenPart();

    [GeneratedRegex(@"(?i)^(карт[аыуе]|картой|card|карты:)$")]
    private static partial Regex CardWord();

    [GeneratedRegex(@"^\d[\d \-]{11,21}\d$")]
    private static partial Regex CardShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^v?\d+(\.\d+)+([\-+][\w.\-]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
}
