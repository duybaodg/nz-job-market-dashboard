namespace Application.Employers;

/// <summary>How the listing was advertised, derived from the reviewed source registry category.</summary>
public enum AdvertiserType
{
    /// <summary>The employer's own board (registry category <c>company</c>/<c>government</c>).</summary>
    DirectEmployer,

    /// <summary>A recruitment agency / staffing firm (registry category <c>recruiter</c>).</summary>
    Agency,

    /// <summary>A job board or aggregator that hosts someone else's advertisement.</summary>
    Board,

    /// <summary>The registry has no usable category.</summary>
    Unknown
}

/// <summary>Whether the actual hiring employer is named.</summary>
public enum EmployerDisclosure
{
    /// <summary>A hiring employer is named (or the employer's own board is the source).</summary>
    Disclosed,

    /// <summary>The advertisement is agency-only; the client is never invented.</summary>
    AgencyOnly,

    /// <summary>No employer information was captured.</summary>
    Unknown
}

public sealed record EmployerClassification(
    string SourceCategory,
    string Advertiser,
    AdvertiserType AdvertiserType,
    string? HiringEmployer,
    EmployerDisclosure Disclosure);

/// <summary>
/// Separates the listing advertiser from the hiring employer. The reviewed
/// <c>crawler/sources/sites.json</c> category decides whether the source is a
/// direct employer, an agency or a board; a small reviewed alias table catches
/// agencies advertising on boards and agencies whose board reports their own
/// trading name in the company field. When the client is not named the employer
/// stays <c>null</c> — the agency's client is never guessed.
/// </summary>
public static class EmployerClassifier
{
    /// <summary>
    /// Reviewed agency trading names and board-facing brands. Matching is
    /// case-insensitive and exact on the trimmed company value; similar names
    /// are deliberately not merged automatically.
    /// </summary>
    public static readonly IReadOnlySet<string> AgencyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "absolute it",
        "alpha recruitment",
        "beyond recruitment",
        "comspek international",
        "consult recruitment",
        "digital garage",
        "h2r technology",
        "hays",
        "hays nz",
        "hays recruitment",
        "madison recruitment",
        "placeme it",
        "potentia",
        "presto resourcing",
        "randstad",
        "randstad new zealand",
        "recruit it",
        "robert walters",
        "robert walters nz",
        "salt",
        "salt nz",
        "talent army",
        "talent international",
        "talent vault",
        "teksystems",
        "teksystems nz",
        "tribe group",
        "tribe recruit",
        "tribe recruitment",
        "tribe tech",
        "younity"
    };

    public static EmployerClassification Classify(string source, string? sourceCategory, string? company)
    {
        var advertiser = (source ?? string.Empty).Trim();
        var category = NormaliseCategory(sourceCategory);
        var advertiserType = category switch
        {
            "company" or "government" => AdvertiserType.DirectEmployer,
            "recruiter" => AdvertiserType.Agency,
            "job-board" or "board" or "aggregator" or "graduate" => AdvertiserType.Board,
            _ => AdvertiserType.Unknown
        };

        var candidate = string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        var candidateIsAgency = candidate is not null && AgencyAliases.Contains(candidate);

        string? employer;
        EmployerDisclosure disclosure;

        switch (advertiserType)
        {
            case AdvertiserType.DirectEmployer:
                // The employer's own board always names the employer: prefer the
                // parsed company, otherwise the source itself.
                employer = candidate ?? advertiser;
                disclosure = string.IsNullOrWhiteSpace(employer)
                    ? EmployerDisclosure.Unknown
                    : EmployerDisclosure.Disclosed;
                break;

            case AdvertiserType.Agency:
                if (candidateIsAgency || candidate is null)
                {
                    employer = null;
                    disclosure = EmployerDisclosure.AgencyOnly;
                }
                else
                {
                    employer = candidate;
                    disclosure = EmployerDisclosure.Disclosed;
                }

                break;

            case AdvertiserType.Board:
                if (candidateIsAgency)
                {
                    employer = null;
                    disclosure = EmployerDisclosure.AgencyOnly;
                }
                else if (candidate is null || NamesMatch(candidate, advertiser))
                {
                    // A board repeating its own name in the company field is not
                    // evidence of an employer, so it stays unknown rather than
                    // being counted as a hiring employer.
                    employer = null;
                    disclosure = EmployerDisclosure.Unknown;
                }
                else
                {
                    employer = candidate;
                    disclosure = EmployerDisclosure.Disclosed;
                }

                break;

            default:
                employer = candidateIsAgency ? null : candidate;
                disclosure = candidate is null || candidateIsAgency
                    ? EmployerDisclosure.Unknown
                    : EmployerDisclosure.Disclosed;
                break;
        }

        return new EmployerClassification(category, advertiser, advertiserType, employer, disclosure);
    }

    public static string NormaliseCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "unknown" : category.Trim().ToLowerInvariant();

    /// <summary>
    /// Loose equality used only to detect a board echoing its own name in the
    /// company field. Punctuation and whitespace are ignored.
    /// </summary>
    private static bool NamesMatch(string left, string right)
    {
        static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Normalise(left);
        var b = Normalise(right);
        return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }
}
