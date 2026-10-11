using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Only the catalog row id describes the model; provider, deployment and prices are read from that
/// <c>Core.Ref.Model</c> row at use so they cannot disagree. Everything else bounds the run.
/// </remarks>
public sealed class FileAgentOptions
{
    public const string SectionName = "FileAgent";

    /// <summary>
    /// The rollback switch: off detaches the tool from every turn but keeps generated files downloadable.
    /// Off by default everywhere since sandbox runs are billed; startup validation runs regardless.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The pinned <c>Core.Ref.Model</c> row. It must be Azure OpenAI: only its Responses route carries the
    /// hosted code interpreter.
    /// </summary>
    public Guid ModelId { get; set; }

    /// <summary>
    /// Ceiling on one whole invocation, independent of the turn's own bounds. A run holds the conversation
    /// lock, so this also bounds how long other turns get 409.
    /// </summary>
    public int ToolTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Stops a looping script persisting unbounded blobs; the surplus is dropped and reported, not failed.
    /// </summary>
    public int MaxArtifactsPerRun { get; set; } = 3;

    /// <summary>
    /// Separate from the turn's ceiling: the agent spends iterations the turn does not (loading skills,
    /// running code, re-checking the artifact).
    /// </summary>
    public int MaxIterationsPerRun { get; set; } = 12;

    /// <summary>
    /// Zero is legitimate, allowing one paid attempt only. The run deadline bounds the total regardless.
    /// </summary>
    public int MaxVerificationRetries { get; set; } = 1;

    /// <summary>
    /// <see langword="null"/> (the default) for no ceiling, since an unchosen ceiling fails on an unpredicted
    /// day. At the ceiling the tool is not attached and the assistant is told why.
    /// </summary>
    public int? MaxRunsPerUserPerDay { get; set; }

    /// <summary>
    /// <see langword="null"/> for no ceiling. Measured from recorded tool-call durations, the queryable proxy
    /// for billed sandbox time.
    /// </summary>
    public int? MaxSandboxSecondsPerUserPerDay { get; set; }
}

public sealed class FileAgentOptionsValidator : AbstractValidator<FileAgentOptions>
{
    public FileAgentOptionsValidator()
    {
        RuleFor(options => options.ModelId).NotEqual(Guid.Empty).WithMessage("FileAgent:ModelId is required.");
        RuleFor(options => options.ToolTimeoutSeconds).InclusiveBetween(30, 1800)
            .WithMessage("FileAgent:ToolTimeoutSeconds must be between 30 and 1800.");
        RuleFor(options => options.MaxArtifactsPerRun).InclusiveBetween(1, 10)
            .WithMessage("FileAgent:MaxArtifactsPerRun must be between 1 and 10.");
        RuleFor(options => options.MaxIterationsPerRun).InclusiveBetween(2, 40)
            .WithMessage("FileAgent:MaxIterationsPerRun must be between 2 and 40.");
        RuleFor(options => options.MaxVerificationRetries).InclusiveBetween(0, 3)
            .WithMessage("FileAgent:MaxVerificationRetries must be between 0 and 3.");
        RuleFor(options => options.MaxRunsPerUserPerDay).InclusiveBetween(1, 10_000)
            .WithMessage("FileAgent:MaxRunsPerUserPerDay must be between 1 and 10000.");
        RuleFor(options => options.MaxSandboxSecondsPerUserPerDay).InclusiveBetween(1, 1_000_000)
            .WithMessage("FileAgent:MaxSandboxSecondsPerUserPerDay must be between 1 and 1000000.");
    }
}
