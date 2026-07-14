namespace Infrastructure.Scrapling;

public sealed class ScraplingOptions
{
    public string PythonExecutable { get; set; } = "python3";
    public string WorkerScriptPath { get; set; } = string.Empty;
    public string FetcherType { get; set; } = "fetcher";
    public bool RobotsTxtObey { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 120;
    public string ProxyUrl { get; set; } = string.Empty;
}
