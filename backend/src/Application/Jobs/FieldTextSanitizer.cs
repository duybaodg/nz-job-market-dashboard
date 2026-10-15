using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// Removes navigation and filter-widget boilerplate from a retained full job
/// description before deterministic field extraction.
///
/// The retained description is the raw page text, which on several NZ boards
/// includes the site's own chrome. Live examples:
/// <list type="bullet">
/// <item>ZEIL keeps a giant "Edit Advanced Filters ... Work Style On Site
/// Remote Hybrid ... Job Schedule ... Annual Wage $0 $300,000+" taxonomy whose
/// whole tail (often the entire 20k cap) is navigation.</item>
/// <item>Trade Me / SEEK pages embed a "Related Jobs Any location Work Remotely
/// Auckland Bay of Plenty ..." widget.</item>
/// </list>
/// Scanning that chrome would assign a work mode (or a salary-filter ceiling) to
/// almost every listing, so it is stripped first — the same reasoning as the P0
/// salary filter-widget guard. The pass is deliberately conservative: it only
/// removes a span when the distinctive widget signature is present.
/// </summary>
public static class FieldTextSanitizer
{
    /// <summary>The retained description cap used elsewhere; keeps sanitising bounded.</summary>
    public const int MaxChars = 24_000;

    private static readonly Regex AdvancedFiltersWidget = new(
        @"(?is)\b(?:edit\s+)?advanced\s+filters\b(?=.{0,200}?\bwork\s+style\b).*$",
        RegexOptions.Compiled);

    private static readonly Regex RelatedJobsWidget = new(
        @"(?is)\brelated\s+jobs\b(?=.{0,60}?(?:any\s+location|work\s+remotely)).*$",
        RegexOptions.Compiled);

    private static readonly Regex SalaryFilterWidget = new(
        @"(?is)\b(?:annual\s+wage|salary\s+range|salary\s+filter)\b.{0,140}",
        RegexOptions.Compiled);

    private static readonly Regex WorkStyleWidget = new(
        @"(?is)\bwork\s+style\s*[:\-]?\s*(?:on[\s-]?site\s+remote\s+hybrid|remote\s+hybrid\s+on[\s-]?site).{0,200}",
        RegexOptions.Compiled);

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Length <= MaxChars ? text : text[..MaxChars];
        value = AdvancedFiltersWidget.Replace(value, " ");
        value = RelatedJobsWidget.Replace(value, " ");
        value = SalaryFilterWidget.Replace(value, " ");
        value = WorkStyleWidget.Replace(value, " ");
        return value;
    }
}
