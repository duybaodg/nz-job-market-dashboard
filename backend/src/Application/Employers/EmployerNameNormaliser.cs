using System.Text.RegularExpressions;

namespace Application.Employers;

/// <summary>
/// The single normalisation used for canonical employer identity. Matching is
/// exact after case and whitespace normalisation only — punctuation is kept so
/// "F&amp;P" and "FP" are different names and are never merged automatically.
/// </summary>
public static partial class EmployerNameNormaliser
{
    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ").ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
