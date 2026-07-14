namespace Application.Normalisation;

public interface INormalisationService
{
    string StandardiseRegion(string? location);
    string StandardiseSeniority(string? title);
}

public sealed class NormalisationService : INormalisationService
{
    public string StandardiseRegion(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "Unknown";
        }

        var value = location.ToLowerInvariant();

        if (value.Contains("auckland")) return "Auckland";
        if (value.Contains("wellington")) return "Wellington";
        if (value.Contains("christchurch") || value.Contains("canterbury")) return "Canterbury";
        if (value.Contains("hamilton") || value.Contains("waikato")) return "Waikato";
        if (value.Contains("dunedin") || value.Contains("otago")) return "Otago";
        if (value.Contains("tauranga") || value.Contains("bay of plenty")) return "Bay of Plenty";

        return "Other";
    }

    public string StandardiseSeniority(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown";
        }

        var value = title.ToLowerInvariant();

        if (value.Contains("graduate")) return "Graduate";
        if (value.Contains("junior") || value.Contains("entry")) return "Junior";
        if (value.Contains("senior")) return "Senior";
        if (value.Contains("lead") || value.Contains("principal")) return "Lead";

        return "Intermediate";
    }
}
