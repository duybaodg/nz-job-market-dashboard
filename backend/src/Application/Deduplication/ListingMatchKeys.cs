using System.Text;

namespace Application.Deduplication;

/// <summary>
/// Canonical comparison keys for the dedup rules. Kept public so each boundary
/// (title punctuation, salary text equality, location city) is unit-testable.
/// </summary>
public static class ListingMatchKeys
{
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        var pendingSpace = false;
        foreach (var character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Salary text reduced to letters/digits so "$100,000" and "$100000" compare
    /// equal while genuinely different amounts stay different.
    /// </summary>
    public static string NormalizeSalary(string? salaryText)
    {
        if (string.IsNullOrWhiteSpace(salaryText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(salaryText.Length);
        foreach (var character in salaryText)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static string NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        return string.Join(' ', location
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }
}
