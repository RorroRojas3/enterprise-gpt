using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Settings for the fabricated weather tool, bound from the <c>Tools:Weather</c> configuration
/// section and validated at startup.
/// </summary>
/// <remarks>
/// The tool exists to exercise the activity timeline — cards, sub-statuses, durations, and the
/// failed state — without standing up an MCP server or uploading a document. It answers with
/// invented data, so it is off unless a deployment asks for it: attached to every production turn
/// it is a tool the model would call and then answer from.
/// </remarks>
public sealed class WeatherToolOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Tools:Weather";

    /// <summary>
    /// Gets or sets a value indicating whether the tool is attached to turns.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets how long a lookup pretends to take.
    /// </summary>
    /// <remarks>
    /// Artificial latency, and the point of it: an instant tool call completes inside one stream
    /// flush, so the running ring and the sub-status bullets never paint and the card appears
    /// already finished. Anything around half a second is enough to watch the states change.
    /// </remarks>
    [Range(0, 10_000)]
    public int DelayMilliseconds { get; set; } = 600;
}
