using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Only the catalog row id describes the summarizer; window, provider, deployment and prices are read from
/// that <c>Core.Ref.Model</c> row at use, so edits apply without a redeploy. The rest tunes the algorithm.
/// </remarks>
public sealed class SummarizationOptions
{
    public const string SectionName = "Summarization";

    /// <summary>
    /// The rollback switch: off detaches the tool from every turn; existing summaries are served again once
    /// it is back on. Off by default everywhere since it spends money; startup validation runs regardless.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Pinned, not the conversation's model: summarization is compression, and its cost should not scale with
    /// the user's choice of chat model.
    /// </summary>
    public Guid ModelId { get; set; }

    /// <summary>
    /// Share of the per-call budget a prompt may use, scaling with the window. It absorbs estimate error:
    /// deployment names are not tokenizer model names, so counting falls back and can under-report.
    /// </summary>
    public double SafetyFraction { get; set; } = 0.85;

    /// <summary>
    /// Below the per-call budget because a unit is sized before its framing is added, and token counts are
    /// not additive across the splitter's joins.
    /// </summary>
    public double MapUnitFraction { get; set; } = 0.90;

    /// <summary>
    /// Per document's map phase, so one document cannot monopolise the job processor's shared gate.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>An unmeasured placeholder that stops one stalled call holding a processing slot.</summary>
    public int CallTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Re-summarize passes over still-overflowing partials; past it the run fails naming the limit instead of
    /// looping until an unrelated timeout.
    /// </summary>
    public int MaxCollapsePasses { get; set; } = 5;

    /// <summary>Kept small because every retry is billed; zero is a supported choice.</summary>
    public int MaxCallRetries { get; set; } = 2;

    /// <summary>
    /// Bounds the whole run, which holds the conversation lock and an open SSE stream. A breach is reported
    /// to the model as a message it can relay, not as a failed turn.
    /// </summary>
    public int ToolTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Checked before the first map call, so an oversized document costs nothing. Near-inert at a
    /// million-token window; it matters if the summarizer moves to a small-window model.
    /// </summary>
    public int MaxMapUnits { get; set; } = 40;

    /// <summary>
    /// A digest summarizes every unsummarized document in scope, so a large scope would mean unbounded runs;
    /// the refusal points at summarizing documents individually.
    /// </summary>
    public int MaxDigestDocuments { get; set; } = 25;
}

public sealed class SummarizationOptionsValidator : AbstractValidator<SummarizationOptions>
{
    public SummarizationOptionsValidator()
    {
        RuleFor(options => options.ModelId).NotEqual(Guid.Empty).WithMessage("Summarization:ModelId is required.");
        RuleFor(options => options.SafetyFraction).InclusiveBetween(0.1d, 1d)
            .WithMessage("Summarization:SafetyFraction must be between 0.1 and 1.");
        RuleFor(options => options.MapUnitFraction).InclusiveBetween(0.1d, 1d)
            .WithMessage("Summarization:MapUnitFraction must be between 0.1 and 1.");
        RuleFor(options => options.MaxConcurrency).InclusiveBetween(1, 32)
            .WithMessage("Summarization:MaxConcurrency must be between 1 and 32.");
        RuleFor(options => options.CallTimeoutSeconds).InclusiveBetween(1, 1800)
            .WithMessage("Summarization:CallTimeoutSeconds must be between 1 and 1800.");
        RuleFor(options => options.MaxCollapsePasses).InclusiveBetween(1, 20)
            .WithMessage("Summarization:MaxCollapsePasses must be between 1 and 20.");
        RuleFor(options => options.MaxCallRetries).InclusiveBetween(0, 5)
            .WithMessage("Summarization:MaxCallRetries must be between 0 and 5.");
        RuleFor(options => options.ToolTimeoutSeconds).InclusiveBetween(30, 1800)
            .WithMessage("Summarization:ToolTimeoutSeconds must be between 30 and 1800.");
        RuleFor(options => options.MaxMapUnits).InclusiveBetween(1, 1000)
            .WithMessage("Summarization:MaxMapUnits must be between 1 and 1000.");
        RuleFor(options => options.MaxDigestDocuments).InclusiveBetween(1, 200)
            .WithMessage("Summarization:MaxDigestDocuments must be between 1 and 200.");
        // A run deadline below one call's own timeout kills every run before its first call returns.
        RuleFor(options => options.ToolTimeoutSeconds).GreaterThanOrEqualTo(options => options.CallTimeoutSeconds)
            .WithMessage("Summarization:ToolTimeoutSeconds must be at least Summarization:CallTimeoutSeconds.");
    }
}
