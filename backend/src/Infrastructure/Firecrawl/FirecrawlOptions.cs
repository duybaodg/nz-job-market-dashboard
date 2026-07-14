namespace Infrastructure.Firecrawl;

public sealed class FirecrawlOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.firecrawl.dev/v2";
    public int TimeoutSeconds { get; set; } = 60;
}
