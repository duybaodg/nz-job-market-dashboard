using System.Security.Cryptography;
using System.Text;

namespace Application.Deduplication;

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
    DateTime? ClosingDate);

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
