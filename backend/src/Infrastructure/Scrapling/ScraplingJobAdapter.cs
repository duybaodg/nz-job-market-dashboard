using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Crawling;
using Microsoft.Extensions.Options;

namespace Infrastructure.Scrapling;

public sealed class ScraplingJobAdapter(IOptions<ScraplingOptions> options) : IJobSourceAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ScraplingOptions options = options.Value;

    public string SourceName => "Scrapling";

    public async Task<SourceFetchResult> FetchJobsAsync(
        JobSearchRequest request,
        CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath();

        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"Scrapling worker script was not found at '{scriptPath}'.");
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("--source");
        process.StartInfo.ArgumentList.Add(SourceName);
        process.StartInfo.ArgumentList.Add("--url");
        process.StartInfo.ArgumentList.Add(request.Url);
        process.StartInfo.ArgumentList.Add("--fetcher-type");
        process.StartInfo.ArgumentList.Add(options.FetcherType);

        if (options.RobotsTxtObey)
        {
            process.StartInfo.ArgumentList.Add("--robots-txt-obey");
        }

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            process.StartInfo.ArgumentList.Add("--proxy-url");
            process.StartInfo.ArgumentList.Add(options.ProxyUrl);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stdout.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 10)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"Scrapling crawl timed out after {options.TimeoutSeconds} seconds.");
        }

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Scrapling worker failed with exit code {process.ExitCode}."
                : error);
        }

        var json = stdout
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Scrapling worker did not return JSON output.");
        }

        var result = JsonSerializer.Deserialize<SourceFetchResult>(json, JsonOptions);

        return result ?? throw new InvalidOperationException("Scrapling worker returned empty or invalid JSON.");
    }

    private string ResolveScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(options.WorkerScriptPath))
        {
            return Path.GetFullPath(options.WorkerScriptPath);
        }

        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(currentDirectory, "crawler", "adapters", "scrapling", "scrapling_worker.py"),
            Path.Combine(currentDirectory, "..", "..", "..", "crawler", "adapters", "scrapling", "scrapling_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "crawler", "adapters", "scrapling", "scrapling_worker.py")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? Path.GetFullPath(candidates[0]);
    }

    private string ResolvePythonExecutable()
    {
        if (string.IsNullOrWhiteSpace(options.PythonExecutable))
        {
            return "python3";
        }

        if (!options.PythonExecutable.Contains('/') && !options.PythonExecutable.Contains('\\'))
        {
            return options.PythonExecutable;
        }

        if (Path.IsPathRooted(options.PythonExecutable))
        {
            return options.PythonExecutable;
        }

        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(currentDirectory, options.PythonExecutable),
            Path.Combine(currentDirectory, "..", "..", "..", options.PythonExecutable)
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? options.PythonExecutable;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited between timeout and kill.
        }
    }
}
