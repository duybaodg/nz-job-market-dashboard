namespace Domain.Entities;

/// <summary>
/// Round 21: a dated, append-only record of a source-registry state change.
/// The registry rows themselves only hold the current state, so without this
/// log a historical source addition, disable or retirement can only be inferred
/// from crawl observations. The first event for each source is a truthful
/// "state as of 2026-11-05" baseline; earlier changes remain unknown and are
/// never reconstructed.
/// </summary>
public sealed class RegistryEvent
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;

    /// <summary>
    /// <c>state-as-of</c>, <c>added</c>, <c>enabled</c>, <c>disabled</c>,
    /// <c>retired</c> or <c>revived</c>.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Registry enabled state after the event.</summary>
    public bool Enabled { get; set; }

    /// <summary>Registry retirement timestamp after the event, or null when current.</summary>
    public DateTime? RetiredAt { get; set; }

    /// <summary>Pacific/Auckland date on which the state applied.</summary>
    public DateOnly EventDate { get; set; }

    /// <summary>When the recorder observed and wrote this event (UTC).</summary>
    public DateTime RecordedAtUtc { get; set; }

    /// <summary>Human-readable reason. Never contains operational secrets.</summary>
    public string Reason { get; set; } = string.Empty;

    public string Actor { get; set; } = "system";
}
