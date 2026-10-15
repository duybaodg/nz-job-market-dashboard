using System.Text;

namespace Application.Deduplication;

/// <summary>
/// Shingled-token Jaccard similarity used as the description guard for the
/// conservative dedup rules. Text is lower-cased and reduced to alphanumeric
/// word tokens, then overlapping 3-word shingles are compared.
/// </summary>
public static class DescriptionSimilarity
{
    /// <summary>The brief's strong-evidence threshold: merge only at or above this.</summary>
    public const double MergeThreshold = 0.8;

    public const int ShingleSize = 3;

    public static double Jaccard(string? left, string? right) =>
        Jaccard(Shingles(left), Shingles(right));

    public static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var shingle in left)
        {
            if (right.Contains(shingle))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    public static IReadOnlySet<string> Shingles(string? text)
    {
        var tokens = Tokenise(text);
        var shingles = new HashSet<string>(StringComparer.Ordinal);
        if (tokens.Count == 0)
        {
            return shingles;
        }

        if (tokens.Count < ShingleSize)
        {
            foreach (var token in tokens)
            {
                shingles.Add(token);
            }

            return shingles;
        }

        for (var index = 0; index + ShingleSize <= tokens.Count; index++)
        {
            var builder = new StringBuilder();
            for (var offset = 0; offset < ShingleSize; offset++)
            {
                if (offset > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(tokens[index + offset]);
            }

            shingles.Add(builder.ToString());
        }

        return shingles;
    }

    private static List<string> Tokenise(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }
}
