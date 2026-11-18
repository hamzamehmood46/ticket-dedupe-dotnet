using System.Text.RegularExpressions;

namespace Dedupe.Core;

/// <summary>Turns cleaned ticket text into a vector. Swap this for a real embedding model; everything else only sees vectors.</summary>
public interface IEmbedder
{
    int Dimensions { get; }

    float[] Embed(string cleanedText);
}

/// <summary>Small vector helpers shared by the index, the finder and the evaluator.</summary>
public static class Vectors
{
    public static bool IsFinite(float[] v) => v.Length > 0 && v.All(float.IsFinite);

    public static double Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += (double)x * x;
        }

        return Math.Sqrt(sum);
    }

    public static bool IsZero(float[] v) => Norm(v) == 0;

    /// <summary>Cosine similarity, or 0 when either vector has no length (so an empty ticket matches nothing).</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vectors have different lengths ({a.Length} and {b.Length}).");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
/// A stand-in embedder: hashed word and word-pair counts weighted by how rare each is across the tickets it was fitted
/// on (idf), then scaled to length 1. It matches words, not meaning. The synonym list below is short and generic, and
/// anything it does not cover is invisible to it.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public const int DefaultDimensions = 2048;

    private const double BigramWeight = 0.7;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at", "for", "with", "from", "by", "as",
        "is", "are", "was", "were", "be", "been", "am", "it", "its", "this", "that", "these", "those", "i", "me", "my",
        "we", "our", "you", "your", "they", "their", "them", "have", "has", "had", "do", "does", "did", "can", "could",
        "cannot", "would", "will", "please", "hi", "hello", "hey", "thanks", "thank", "there", "here", "so", "just", "now",
        "still", "again", "very", "when", "what", "how", "which", "who", "up", "out", "into", "about", "any", "some",
        "get", "got", "also", "since", "ve", "ll", "re",
    };

    // Whole-word replacements, applied before stemming. The right-hand side is a canonical word, not a real one.
    private static readonly Dictionary<string, string> Canon = new(StringComparer.Ordinal)
    {
        ["passcode"] = "password",
        ["signin"] = "login",
        ["logon"] = "login",
        ["billed"] = "charge",
        ["alert"] = "notification",
        ["alerts"] = "notification",
        ["reimburse"] = "refund",
        ["reimbursed"] = "refund",
        ["download"] = "export",
        ["downloads"] = "export",
        ["delayed"] = "delay",
        ["delays"] = "delay",
        ["late"] = "delay",
        ["double"] = "twice",
        ["invisible"] = "unreadable",
        ["faint"] = "unreadable",
        ["disabled"] = "locked",
        ["suspended"] = "locked",
        ["blocked"] = "locked",
        ["mfa"] = "twofactor",
        ["2fa"] = "twofactor",
    };

    // Multi-word phrases that mean one thing. Order matters: logout is replaced before login.
    private static readonly (Regex Pattern, string Replacement)[] Phrases =
    [
        (new Regex(@"\b(?:sign|log)(?:s|ed|ged|ging|ing)?[\s-]*(?:me\s+)?out\b", RegexOptions.Compiled), "logout"),
        (new Regex(@"\b(?:sign|log)(?:s|ed|ged|ging|ing)?[\s-]*(?:me\s+)?in\b", RegexOptions.Compiled), "login"),
        (new Regex(@"\btwo[\s-]?factor\b", RegexOptions.Compiled), "twofactor"),
        (new Regex(@"\b(?:time[ds]?[\s-]?out|timeouts?)\b", RegexOptions.Compiled), "timeout"),
        (new Regex(@"\btwo times\b", RegexOptions.Compiled), "twice"),
        (new Regex(@"\bmoney back\b", RegexOptions.Compiled), "refund"),
        (new Regex(@"\brate[\s-]?limit(?:s|ed|ing)?\b", RegexOptions.Compiled), "ratelimit"),
        (new Regex(@"\b(?:can ?not|can't|cant|couldn't|unable to) read\b|\bhard to (?:read|see)\b", RegexOptions.Compiled), "unreadable"),
    ];

    private static readonly Regex Word = new(@"[a-z0-9]+", RegexOptions.Compiled);

    private readonly float[] _idf;

    public HashingEmbedder(int dimensions = DefaultDimensions, float[]? idf = null)
    {
        if (dimensions < 16 || dimensions > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Use 16 to 65,536 dimensions.");
        }

        if (idf is not null && idf.Length != dimensions)
        {
            throw new ArgumentException("The idf table must have one entry per dimension.", nameof(idf));
        }

        Dimensions = dimensions;
        _idf = idf ?? Enumerable.Repeat(1f, dimensions).ToArray();
    }

    public int Dimensions { get; }

    /// <summary>
    /// Builds an embedder whose weights come from these documents: a word in nearly every ticket ("not", "working")
    /// counts for little, a rare one for a lot. If the documents change a lot, fit again and re-embed everything.
    /// </summary>
    public static HashingEmbedder Fit(IEnumerable<string> cleanedDocuments, int dimensions = DefaultDimensions)
    {
        var df = new int[dimensions];
        var n = 0;
        foreach (var doc in cleanedDocuments)
        {
            n++;
            foreach (var bucket in Features(doc, dimensions).Select(f => f.Bucket).Distinct())
            {
                df[bucket]++;
            }
        }

        var idf = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            idf[i] = (float)(Math.Log((n + 1.0) / (df[i] + 1.0)) + 1.0);
        }

        return new HashingEmbedder(dimensions, idf);
    }

    public float[] Embed(string cleanedText)
    {
        var counts = new Dictionary<int, (int Count, double Weight)>();
        foreach (var (bucket, weight) in Features(cleanedText ?? "", Dimensions))
        {
            counts[bucket] = counts.TryGetValue(bucket, out var c) ? (c.Count + 1, weight) : (1, weight);
        }

        var vector = new float[Dimensions];
        double norm = 0;
        foreach (var (bucket, (count, weight)) in counts)
        {
            var value = (1 + Math.Log(count)) * weight * _idf[bucket];
            vector[bucket] = (float)value;
            norm += value * value;
        }

        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }

    /// <summary>The words a piece of cleaned text is reduced to: phrases merged, stopwords dropped, synonyms and endings folded.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        foreach (var (pattern, replacement) in Phrases)
        {
            t = pattern.Replace(t, replacement);
        }

        var tokens = new List<string>();
        foreach (Match m in Word.Matches(t))
        {
            var w = m.Value;
            if (w.Length < 2 || Stopwords.Contains(w))
            {
                continue;
            }

            if (Canon.TryGetValue(w, out var canon))
            {
                w = canon;
            }

            tokens.Add(Stem(w));
        }

        return tokens;
    }

    private static string Stem(string w)
    {
        if (w.Length <= 4 || w.All(char.IsDigit))
        {
            return w;
        }

        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (w.EndsWith(suffix, StringComparison.Ordinal) && w.Length - suffix.Length >= 3)
            {
                if (suffix == "s" && w.EndsWith("ss", StringComparison.Ordinal))
                {
                    break;
                }

                w = w[..^suffix.Length];
                break;
            }
        }

        return w.Length > 4 && w.EndsWith('e') ? w[..^1] : w;
    }

    private static IEnumerable<(int Bucket, double Weight)> Features(string text, int dimensions)
    {
        var tokens = Tokens(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            yield return (Bucket(tokens[i], dimensions), 1.0);
            if (i + 1 < tokens.Count)
            {
                yield return (Bucket(tokens[i] + "_" + tokens[i + 1], dimensions), BigramWeight);
            }
        }
    }

    /// <summary>FNV-1a, so the same word always lands in the same place on every machine.</summary>
    private static int Bucket(string s, int dimensions)
    {
        var hash = 2166136261u;
        foreach (var c in s)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)dimensions);
    }
}
