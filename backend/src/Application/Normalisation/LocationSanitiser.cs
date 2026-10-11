using System.Text.RegularExpressions;

namespace Application.Normalisation;

/// <summary>
/// Result of sanitising a raw location string.
/// <see cref="Location"/> is the cleaned, place-shaped location (or <c>null</c>
/// when nothing usable was found) and <see cref="RemovedRemainder"/> is the
/// content that was cut off, so the caller can preserve it in the description.
/// </summary>
public sealed record LocationSanitisation(string? Location, string? RemovedRemainder);

/// <summary>
/// Shared location sanitizer applied at normalisation time for every parser.
///
/// Some sources put a whole detail block in the location field: Comspek
/// (<c>"Wellington Job type Permanent Specialisation Team Leaders Reference
/// BH-2844 Job description …"</c>) and SEEK (<c>"Nelson, Nelson Region, New
/// Zealand We also welcome applications …"</c>). The region mapper then sees a
/// giant string and the dashboard shows description text as a location.
///
/// A location is truncated at the first non-location token (a labelled field
/// such as "Job type"/"Specialisation"/"Reference"/"Job description", a sentence
/// such as "We also …", a sentence boundary), then limited to a handful of
/// comma-separated segments and roughly 80 characters. The removed text is not
/// discarded: callers prepend it to the description.
/// </summary>
public static class LocationSanitiser
{
    public const int MaxLength = 80;
    public const int MaxSegments = 4;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    //: Starts a new field/paragraph after a location. The leading \s* means the
    //: cut happens before the separating space.
    private static readonly Regex StopMarker = new(
        """
        (?ix)
        \s*
        (?:
            job\s+type
          | job\s+description
          | speciali[sz]ation
          | reference
          | discipline
          | salary
          | category
          | posted
          | employment\s+type
          | work\s+type
          | contract\s+type
          | we\s+(?:also|are|have|offer|provide|believe|welcome)
          | the\s+role
          | about\s+(?:the|us|you|this)
          | key\s+responsibilit\w*
          | what\s+you
          | apply\s+now
          | please\s+(?:note|apply|contact)
          | join\s+(?:our|us|the)
        )\b
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);

    //: A sentence boundary (". Next sentence") that is not a common abbreviation.
    private static readonly Regex SentenceBoundary = new(
        @"(?<=\w)[.!?]\s+(?=[A-Z])",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "st", "mt", "dr", "mr", "mrs", "ms", "no", "ave", "rd", "po", "sq", "co", "inc", "ltd"
    };

    public static LocationSanitisation Sanitise(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return new LocationSanitisation(null, null);
        }

        var original = Whitespace.Replace(location, " ").Trim();

        var value = original;
        var marker = StopMarker.Match(value);
        if (marker.Success && marker.Index > 0)
        {
            value = value[..marker.Index];
        }

        // Only look for a sentence boundary when the value is still too long;
        // this avoids truncating place names such as "St. Heliers".
        if (value.Length > MaxLength)
        {
            var boundary = FindSentenceBoundary(value);
            if (boundary > 0)
            {
                value = value[..boundary];
            }
        }

        value = LimitSegments(value);
        value = HardCap(value);
        var clean = value.Trim().Trim('.', ',', ';', '|', '-', ' ', '\t');

        if (clean.Length == 0)
        {
            return new LocationSanitisation(null, original);
        }

        var remainder = clean.Length < original.Length
            ? original[clean.Length..].Trim().Trim(',', ';', '|', '-', ' ', '\t')
            : string.Empty;

        return new LocationSanitisation(clean, string.IsNullOrWhiteSpace(remainder) ? null : remainder);
    }

    /// <summary>
    /// Prepends a removed location remainder to a description without creating
    /// an obvious duplicate. Used by ingestion and the one-off repair pass.
    /// </summary>
    public static string? PrependRemainder(string? remainder, string? description)
    {
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return description;
        }

        var trimmed = remainder.Trim();
        var text = description?.Trim() ?? string.Empty;
        if (text.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return string.IsNullOrEmpty(text) ? trimmed : $"{trimmed} {text}";
    }

    private static int FindSentenceBoundary(string value)
    {
        foreach (Match match in SentenceBoundary.Matches(value))
        {
            var tokenStart = match.Index - 1;
            while (tokenStart > 0 && char.IsLetter(value[tokenStart - 1]))
            {
                tokenStart--;
            }

            var token = value[tokenStart..match.Index];
            if (!Abbreviations.Contains(token))
            {
                return match.Index + 1;
            }
        }

        return -1;
    }

    private static string LimitSegments(string value)
    {
        var segments = value.Split(',');
        if (segments.Length <= MaxSegments)
        {
            return value;
        }

        return string.Join(",", segments.Take(MaxSegments)).TrimEnd();
    }

    private static string HardCap(string value)
    {
        if (value.Length <= MaxLength)
        {
            return value;
        }

        var window = value[..MaxLength];
        var separator = window.LastIndexOfAny([',', ';']);
        if (separator > 0)
        {
            return window[..separator];
        }

        var space = window.LastIndexOf(' ');
        return space > 0 ? window[..space] : window;
    }
}
