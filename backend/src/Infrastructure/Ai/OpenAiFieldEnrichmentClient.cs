using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Application.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>
/// OpenAI-compatible chat-completions client for the optional AI enrichment
/// pass. It requests a strict JSON object and parses the answer defensively:
/// any transport error, non-success status, missing choice, or unparsable
/// content returns null so the caller logs and skips the listing.
///
/// The client is constructed from an <see cref="HttpClient"/> supplied by
/// <c>AddHttpClient</c>; unit tests inject a mocked <c>HttpMessageHandler</c>,
/// so no test ever calls a real endpoint.
/// </summary>
public sealed class OpenAiFieldEnrichmentClient(
    HttpClient httpClient,
    IOptions<AiFieldEnrichmentOptions> options,
    ILogger<OpenAiFieldEnrichmentClient>? logger = null) : IAiFieldEnrichmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SystemPrompt =
        "You extract structured fields from New Zealand job advertisements. "
        + "Reply with ONLY a JSON object (no markdown) with these keys: "
        + "salaryMin (number or null), salaryMax (number or null), currency (string or null), "
        + "period (\"Year\", \"Hour\", \"Day\" or null), workMode (\"Remote\", \"Hybrid\", \"On-site\" or null), "
        + "seniority (\"Junior\", \"Intermediate\", \"Senior\", \"Lead\" or null), confidence (number 0..1). "
        + "Use null when the advertisement does not state the field; never invent a salary. "
        + "Keep an hourly or daily rate on its own period rather than annualising it. "
        + "Treat 'flexible working hours' as a hours benefit, not a location. "
        + "Seniority reflects the seniority of the role, not a company name. Reply with JSON only.";

    private readonly AiFieldEnrichmentOptions settings = options.Value;

    public async Task<AiFieldEnrichmentResponse?> ExtractAsync(
        AiFieldEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = settings.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = $"Title: {request.Title}\nLocation: {request.Location ?? "unknown"}\n"
                        + $"Advertised salary text: {request.SourceSalaryText ?? "none"}\n"
                        + $"Description:\n{request.Description ?? "none"}"
                }
            }
        };

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = content };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("AI enrichment endpoint returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(body, settings.Model, logger);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "AI enrichment HTTP call failed");
            return null;
        }
    }

    /// <summary>Parses a chat-completions body defensively; exposed for tests.</summary>
    public static AiFieldEnrichmentResponse? ParseResponse(
        string? body,
        string model,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var content = choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var json = StripFences(content);
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            return new AiFieldEnrichmentResponse(
                ReadDecimal(root, "salaryMin"),
                ReadDecimal(root, "salaryMax"),
                ReadString(root, "currency"),
                ReadString(root, "period"),
                ReadString(root, "workMode"),
                ReadString(root, "seniority"),
                ReadDecimal(root, "confidence"),
                ReadString(root, "model") ?? model);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(exception, "AI enrichment returned unparsable JSON");
            return null;
        }
    }

    private static string StripFences(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstNewline = value.IndexOf('\n');
        if (firstNewline >= 0)
        {
            value = value[(firstNewline + 1)..];
        }

        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? value[..lastFence].Trim() : value.Trim();
    }

    private static decimal? ReadDecimal(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement element)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }

        element = default;
        return false;
    }
}
