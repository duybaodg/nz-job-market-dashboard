using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Infrastructure.Firecrawl;

/// <summary>
/// Google-style robots.txt matching, ported line-for-line from
/// <c>crawler/adapters/scrapling/robots.py</c>.
///
/// The Python Scrapling worker enforces robots.txt on every fetch. The .NET
/// Firecrawl adapter is a separate single-URL path that the worker never sees,
/// so it must enforce the same rules itself. Keeping the algorithms identical
/// means a URL allowed by one path is allowed by the other (and, more
/// importantly, a disallowed URL is refused by both). <see cref="RobotsTxtPolicy"/>
/// fetches and caches the file; this class is pure and unit-tested against the
/// same SEEK/Trade Me fixtures as the Python matcher.
/// </summary>
public static class RobotsTxtMatcher
{
    public sealed record RobotsRule(bool Allow, string Pattern);

    public sealed record RobotsGroup(IReadOnlyList<string> Agents, IReadOnlyList<RobotsRule> Rules);

    public sealed record RobotsRules(IReadOnlyList<RobotsGroup> Groups)
    {
        public static RobotsRules Empty { get; } = new(Array.Empty<RobotsGroup>());

        public bool IsEmpty => Groups.Count == 0;
    }

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    public static RobotsRules Parse(string? text)
    {
        var groups = new List<RobotsGroup>();
        var agents = new List<string>();
        var rules = new List<RobotsRule>();

        void Flush()
        {
            if (agents.Count > 0)
            {
                groups.Add(new RobotsGroup(agents.ToArray(), rules.ToArray()));
            }

            agents.Clear();
            rules.Clear();
        }

        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0 || !line.Contains(':'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();

            if (field == "user-agent")
            {
                if (rules.Count > 0)
                {
                    Flush();
                }

                agents.Add(value.ToLowerInvariant());
            }
            else if (field is "allow" or "disallow")
            {
                if (agents.Count == 0)
                {
                    agents.Add("*");
                }

                rules.Add(new RobotsRule(field == "allow", value));
            }
        }

        Flush();
        return new RobotsRules(groups);
    }

    public static bool CanFetch(RobotsRules rules, string userAgent, string url)
    {
        if (rules.IsEmpty)
        {
            return true;
        }

        var lowered = (userAgent ?? string.Empty).ToLowerInvariant();
        var bestScore = -1;
        IReadOnlyList<RobotsRule> chosen = Array.Empty<RobotsRule>();

        foreach (var group in rules.Groups)
        {
            int? score = null;
            foreach (var agent in group.Agents)
            {
                if (agent == "*")
                {
                    score = Math.Max(score ?? 0, 0);
                }
                else if (agent.Length > 0 && lowered.Contains(agent, StringComparison.Ordinal))
                {
                    score = Math.Max(score ?? 0, agent.Length);
                }
            }

            if (score is null)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score.Value;
                chosen = group.Rules;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var path = string.IsNullOrEmpty(parsed.AbsolutePath) ? "/" : parsed.AbsolutePath;
        if (!string.IsNullOrEmpty(parsed.Query))
        {
            path = $"{path}{parsed.Query}";
        }

        var matches = new List<(int Length, bool Allow)>();
        foreach (var rule in chosen)
        {
            var regex = PatternToRegex(rule.Pattern);
            if (regex is not null && regex.IsMatch(path))
            {
                matches.Add((rule.Pattern.Length, rule.Allow));
            }
        }

        if (matches.Count == 0)
        {
            return true;
        }

        var longest = matches.Max(match => match.Length);
        // Equal-length Allow beats Disallow.
        return matches.Any(match => match.Length == longest && match.Allow);
    }

    private static Regex? PatternToRegex(string pattern)
    {
        return RegexCache.GetOrAdd(pattern, static value =>
        {
            if (value.Length == 0)
            {
                return null;
            }

            var anchoredEnd = value.EndsWith('$');
            var body = anchoredEnd ? value[..^1] : value;
            var escaped = Regex.Escape(body).Replace(@"\*", ".*", StringComparison.Ordinal);
            // Collapse runs of wildcards (Trade Me's live file has a long run of
            // '*'): ".*.*" and ".*" match the same language, and one wildcard per
            // run avoids exponential backtracking.
            escaped = Regex.Replace(escaped, @"(?:\.\*)+", ".*");
            return new Regex(
                "^" + escaped + (anchoredEnd ? "$" : string.Empty),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        });
    }
}
