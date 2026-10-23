using System.Security.Cryptography;
using System.Text;

namespace Application.Deduplication;

/// <summary>A skill emitted by the Python worker alongside a raw job.</summary>
public sealed record RawJobSkill(string Name, string Type);

public sealed record RawJobInput(
    string Source,
    string? SourceJobId,
    string Title,
    string? Company,
    string? Location,
    string? SalaryText,
    string? DescriptionText,
    string Url,
    DateTime? PostedDate,
    DateTime? ClosingDate,
    IReadOnlyList<RawJobSkill>? Skills = null,
    //: False only when the source itself says the posting is closed. The
    //: crawler still emits it so an existing row can be deactivated honestly;
    //: a new row is never saved as active.
    bool IsActive = true);

public interface IDeduplicationService
{
    string GenerateContentHash(RawJobInput input);
}

public sealed class DeduplicationService : IDeduplicationService
{
    public string GenerateContentHash(RawJobInput input)
    {
        var hashInput = string.IsNullOrWhiteSpace(input.Url)
            ? $"{input.Title}|{input.Company}|{input.Location}|{input.PostedDate:O}"
            : $"{input.Source}|{input.Title}|{input.Company}|{input.Location}|{input.Url}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
