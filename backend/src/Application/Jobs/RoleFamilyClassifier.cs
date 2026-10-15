using System.Text.RegularExpressions;

namespace Application.Jobs;

/// <summary>
/// Deterministic, documented title → role-family mapping. The classifier only
/// reads the role title (never the description) and every result is explicitly
/// marked as inferred, so the original title is always retained alongside it.
/// Order matters: the first matching family wins.
/// </summary>
public static class RoleFamilyClassifier
{
    public const string UnknownFamily = "Other / Unknown";

    public sealed record RoleFamilyMatch(string Family, bool Inferred);

    // Specific families are listed before the broad Software Engineering rule,
    // whose `engineer` token is the fallback for an otherwise unclassified role.
    private static readonly (string Family, Regex Pattern)[] Rules =
    [
        ("Security", Build(@"\b(?:security|cyber|infosec|soc\s+analyst|penetration|vulnerability|isms)\b")),
        ("Data & Analytics", Build(@"\b(?:data\s+(?:scientist|engineer|analyst|architect|warehouse|steward)|analytics|business\s+intelligence|bi\s+(?:developer|analyst)|machine\s+learning|ml\s+engineer|ai\s+engineer|etl|power\s*bi|tableau)\b")),
        ("DevOps & SRE", Build(@"\b(?:devops|dev\s*ops|site\s+reliability|sre|build\s+engineer|release\s+engineer|platform\s+reliability|ci\s*/?\s*cd)\b")),
        ("Testing & QA", Build(@"\b(?:test|qa|quality\s+assurance|automation\s+engineer|sdet)\b")),
        ("Networking", Build(@"\b(?:network|cisco|firewall|telecommunications|telco|voice\s+engineer)\b")),
        ("Architecture", Build(@"\b(?:solutions?\s+architect|enterprise\s+architect|software\s+architect|data\s+architect|technical\s+architect|domain\s+architect)\b")),
        ("Cloud & Infrastructure", Build(@"\b(?:cloud|infrastructure|platform\s+engineer|systems\s+engineer|kubernetes|aws|azure|gcp|vmware|storage\s+engineer|virtualisation)\b")),
        ("Design & UX", Build(@"\b(?:ux|ui|user\s+experience|user\s+research|product\s+designer|interaction\s+design|visual\s+design|service\s+design)\b")),
        ("Product & Project", Build(@"\b(?:product\s+(?:manager|owner)|project\s+manager|programme?\s+manager|program\s+manager|scrum\s+master|delivery\s+(?:manager|lead)|agile\s+coach|pmo)\b")),
        ("Business Analysis", Build(@"\b(?:business\s+analyst|systems?\s+analyst|functional\s+consultant|process\s+analyst)\b")),
        ("Management & Leadership", Build(@"\b(?:engineering\s+manager|head\s+of|director|chief\s+\w+\s+officer|cto|cio|ciso|team\s+lead|tech\s+lead|technical\s+lead|general\s+manager)\b")),
        ("Sales & Pre-sales", Build(@"\b(?:sales|account\s+manager|pre[\s-]?sales|business\s+development|partner\s+manager)\b")),
        ("Support & IT Operations", Build(@"\b(?:support|service\s+desk|help\s*desk|desktop|technician|it\s+operations|systems?\s+administrator|sysadmin|network\s+administrator)\b")),
        ("Software Engineering", Build(@"\b(?:software|developer|programmer|full[\s-]?stack|front[\s-]?end|back[\s-]?end|web\s+developer|mobile|ios|android|react|node|\.net|java|python|golang|engineer)\b"))
    ];

    public static RoleFamilyMatch Classify(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return new RoleFamilyMatch(UnknownFamily, true);
        }

        foreach (var (family, pattern) in Rules)
        {
            if (pattern.IsMatch(value))
            {
                return new RoleFamilyMatch(family, true);
            }
        }

        return new RoleFamilyMatch(UnknownFamily, true);
    }

    public static IReadOnlyList<string> Families { get; } =
        [.. Rules.Select(rule => rule.Family), UnknownFamily];

    private static Regex Build(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
