using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>A deterministic employment-type reading with its provenance.</summary>
public sealed record EmploymentTypeExtraction(string Value, string? Source, decimal? Confidence)
{
    public static readonly EmploymentTypeExtraction Unknown = new("Unknown", null, null);

    public bool HasValue => Value != "Unknown";
}

/// <summary>
/// Deterministic employment-type extraction. A labelled "Employment type: X"
/// field wins, then explicit wording. "Fixed-term" and "Casual" are kept as
/// their own values (the dashboard filter builds its options from the data), and
/// a "permanent full-time" ad is reported as Full-time because that is the
/// working pattern the candidate selects on.
/// </summary>
public static class EmploymentTypeExtractor
{
    private static readonly Regex Labelled = new(
        @"(?ix)\bemployment\s*type\s*[:\-]\s*(?<type>part[\s-]?time|full[\s-]?time|contract|fixed[\s-]?term|casual|permanent|internship|intern)\b",
        RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Value, decimal Confidence)[] Rules =
    [
        (new Regex(@"(?ix)\bfixed[\s-]?term\b", RegexOptions.Compiled), "Fixed-term", 0.8m),
        (new Regex(@"(?ix)\bpart[\s-]?time\b", RegexOptions.Compiled), "Part-time", 0.8m),
        (new Regex(@"(?ix)\bfull[\s-]?time\b", RegexOptions.Compiled), "Full-time", 0.8m),
        (new Regex(@"(?ix)\bcasual\b", RegexOptions.Compiled), "Casual", 0.8m),
        (new Regex(@"(?ix)\b(?:contract(?:or|ing)?|contract\s+role|day\s+rate|hourly\s+rate)\b", RegexOptions.Compiled), "Contract", 0.75m),
        (new Regex(@"(?ix)\bpermanent\b", RegexOptions.Compiled), "Permanent", 0.75m),
        (new Regex(@"(?ix)\b(?:internship|intern)\b", RegexOptions.Compiled), "Internship", 0.75m),
    ];

    public static EmploymentTypeExtraction Extract(string? summary, string? description)
    {
        var text = FieldTextSanitizer.Sanitize(
            string.Join('\n', new[] { summary, description }.Where(part => !string.IsNullOrWhiteSpace(part))));

        if (string.IsNullOrWhiteSpace(text))
        {
            return EmploymentTypeExtraction.Unknown;
        }

        var labelled = Labelled.Match(text);
        if (labelled.Success)
        {
            return new EmploymentTypeExtraction(Normalise(labelled.Groups["type"].Value), "statement", 1.0m);
        }

        foreach (var (pattern, value, confidence) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                return new EmploymentTypeExtraction(value, "description", confidence);
            }
        }

        return EmploymentTypeExtraction.Unknown;
    }

    private static string Normalise(string value) => value.ToLowerInvariant().Replace(" ", "-") switch
    {
        "part-time" => "Part-time",
        "full-time" => "Full-time",
        "contract" => "Contract",
        "fixed-term" => "Fixed-term",
        "casual" => "Casual",
        "permanent" => "Permanent",
        "internship" or "intern" => "Internship",
        _ => "Unknown"
    };
}
