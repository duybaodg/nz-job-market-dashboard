namespace Application.Employers;

/// <summary>
/// Reviewed seed data for canonical employers. Every entry is an explicit,
/// human-reviewed decision: agency trading names and their board-facing
/// variants. It contains no fuzzy rules — only exact normalised strings.
/// </summary>
public static class ReviewedEmployerSeed
{
    /// <summary>
    /// Reviewed alternative name (alias) to the canonical agency it belongs to,
    /// sourced from the registry categories and the agency list in the Round 8
    /// brief. Aliases are stored with their display casing.
    /// </summary>
    public static readonly IReadOnlyList<(string Alias, string Canonical)> AgencySeeds =
    [
        ("Absolute IT", "Absolute IT"),
        ("Alpha Recruitment", "Alpha Recruitment"),
        ("Beyond Recruitment", "Beyond Recruitment"),
        ("Comspek International", "Comspek International"),
        ("Consult Recruitment", "Consult Recruitment"),
        ("Digital Garage", "Digital Garage"),
        ("H2R Technology", "H2R Technology"),
        ("Hays", "Hays"),
        ("Hays NZ", "Hays"),
        ("Hays Recruitment", "Hays"),
        ("Madison Recruitment", "Madison Recruitment"),
        ("PlaceMe IT", "PlaceMe IT"),
        ("Potentia", "Potentia"),
        ("Presto Resourcing", "Presto Resourcing"),
        ("Randstad", "Randstad"),
        ("Randstad New Zealand", "Randstad"),
        ("Recruit IT", "Recruit IT"),
        ("Robert Walters", "Robert Walters"),
        ("Robert Walters NZ", "Robert Walters"),
        ("Salt", "Salt"),
        ("Salt NZ", "Salt"),
        ("Talent Army", "Talent Army"),
        ("Talent International", "Talent International"),
        ("Talent Vault", "Talent Vault"),
        ("TEKsystems", "TEKsystems"),
        ("TEKsystems NZ", "TEKsystems"),
        ("Tribe Group", "Tribe Recruitment"),
        ("Tribe Recruit", "Tribe Recruitment"),
        ("Tribe Recruitment", "Tribe Recruitment"),
        ("Tribe Tech", "Tribe Recruitment"),
        ("Younity", "Younity")
    ];

    /// <summary>Distinct reviewed canonical agency names, used to type employers.</summary>
    public static readonly IReadOnlyList<string> AgencyCanonicalNames =
        AgencySeeds.Select(seed => seed.Canonical).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
}
