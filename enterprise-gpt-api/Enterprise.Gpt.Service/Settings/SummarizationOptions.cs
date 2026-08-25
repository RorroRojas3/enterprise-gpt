using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Settings for document summarization, bound from the <c>Summarization</c> configuration section
/// and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// The section carries the summarizer's catalog row id and nothing else <em>about the summarizer</em>.
/// Its context window, its provider, its deployment name and both of its prices are read from that
/// <c>Core.Ref.Model</c> row at the moment of use, so an administrator editing the row takes effect
/// without a redeploy — and so a fact about the summarizer can never be stated in two places that
/// disagree.
/// </para>
/// <para>
/// Everything else here tunes the <em>algorithm</em> rather than describing the model: how much of
/// the computed budget a call is allowed to use, how many calls may run at once, and how long the
/// engine keeps trying. None of it is derived from a measured baseline, so all of it is bindable —
/// retuning is a configuration change, never a redeploy.
/// </para>
/// </remarks>
public sealed class SummarizationOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Summarization";

    /// <summary>
    /// Gets or sets the <c>Core.Ref.Model</c> identifier of the model that performs every
    /// summarization call.
    /// </summary>
    /// <remarks>
    /// Pinned rather than following the conversation's own selected model: summarization is a
    /// compression task, not a reasoning one, and billing it at whatever an expensive deployment
    /// charges would make the feature's cost scale with a choice the user makes for chat.
    /// </remarks>
    public Guid ModelId { get; set; }

    /// <summary>
    /// Gets or sets the fraction of the raw per-call budget a prompt may actually occupy.
    /// Default: <c>0.85</c>.
    /// </summary>
    /// <remarks>
    /// A multiplier rather than a fixed token margin, so it scales with the summarizer's own window
    /// instead of being wrong for a very large or very small one. It is the headroom that absorbs
    /// the token estimate's own approximation: the deployment name is not a tokenizer-library model
    /// name, so counting falls back to the configured encoding and can under-report.
    /// </remarks>
    [Range(0.1, 1.0)]
    public double SafetyFraction { get; set; } = 0.85;

    /// <summary>
    /// Gets or sets the fraction of the per-call budget one map unit may occupy.
    /// Default: <c>0.90</c>.
    /// </summary>
    /// <remarks>
    /// Strictly below the per-call budget because a map unit is sized before its own framing is
    /// added, and because token counts are not exactly additive across the joins the splitter makes.
    /// </remarks>
    [Range(0.1, 1.0)]
    public double MapUnitFraction { get; set; } = 0.90;

    /// <summary>
    /// Gets or sets how many summarizer calls may be in flight at once for one document's map phase.
    /// Default: <c>4</c>.
    /// </summary>
    /// <remarks>
    /// Bounded so that one large document cannot monopolise the background job processor's own
    /// concurrency gate, which summarization shares with document ingestion.
    /// </remarks>
    [Range(1, 32)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Gets or sets how long a single summarizer call may run before it is abandoned, in seconds.
    /// Default: <c>180</c>.
    /// </summary>
    /// <remarks>
    /// A placeholder rather than a measured value — no pre-production latency baseline exists for
    /// this deployment. Its job is to stop one stalled call holding a processing slot indefinitely.
    /// </remarks>
    [Range(1, 1800)]
    public int CallTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets how many times the collapse loop may re-summarize partial summaries that still
    /// overflow the budget. Default: <c>5</c>.
    /// </summary>
    /// <remarks>
    /// A document whose partial summaries do not shrink fast enough must fail with a message naming
    /// the limit rather than loop until an unrelated timeout kills it silently.
    /// </remarks>
    [Range(1, 20)]
    public int MaxCollapsePasses { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many times a failed summarizer call is retried before its unit fails.
    /// Default: <c>2</c>.
    /// </summary>
    /// <remarks>
    /// Every retry that reaches the model is billed, so the count stays small. Zero disables
    /// retrying entirely, which is a supported choice rather than a degraded one.
    /// </remarks>
    [Range(0, 5)]
    public int MaxCallRetries { get; set; } = 2;
}
