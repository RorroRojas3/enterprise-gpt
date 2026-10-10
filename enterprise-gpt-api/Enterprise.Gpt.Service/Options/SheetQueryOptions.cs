using Enterprise.Gpt.Service.Documents.Sheets;
using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Bounds for the deterministic spreadsheet lookup tool, bound from the <c>SheetQuery</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>Documents:Retrieval</c>'s own caps: this tool returns structured
/// rows rather than prose passages, and it scans a whole sheet rather than a narrowed candidate set,
/// so neither its size nor its time budget follows from retrieval's.
/// </remarks>
public sealed class SheetQueryOptions
{
    /// <summary>
    /// Configuration section this type binds from.
    /// </summary>
    public const string SectionName = "SheetQuery";

    /// <summary>
    /// Gets or sets a value indicating whether the tool is attached to turns.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Off unless a deployment asks for it, and read through <see cref="ISheetQueryOptionsProvider"/> so
    /// flipping it reaches the next turn as soon as the configuration source reloads, without a restart.
    /// Upload, extraction, chunking and storage for spreadsheets are unaffected either way.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Largest number of rows one filter result may return. Default: 50.
    /// </summary>
    public int MaxResultRows { get; set; } = 50;

    /// <summary>
    /// Largest number of characters of cell text one filter result may return. Default: 20,000.
    /// </summary>
    /// <remarks>
    /// The row cap alone bounds nothing about width: fifty rows of a two-hundred-column export is
    /// tens of thousands of characters competing with the answer for the model's context window.
    /// </remarks>
    public int MaxResultCharacters { get; set; } = 20_000;

    /// <summary>
    /// Largest number of groups a grouped aggregate may return. Default: 200.
    /// </summary>
    public int MaxGroups { get; set; } = 200;

    /// <summary>
    /// Largest number of filters one call may supply. Default: 5.
    /// </summary>
    /// <remarks>
    /// Each filter is one more comparison arm evaluated per row of the sheet, and a request past
    /// this is refused rather than trimmed — silently dropping a filter would return rows the model
    /// believes it excluded.
    /// </remarks>
    public int MaxFilters { get; set; } = 5;

    /// <summary>
    /// Per-statement command timeout, in seconds. Default: 10.
    /// </summary>
    /// <remarks>
    /// The tool runs inside a live chat turn, so a degraded query has to fail fast rather than hold
    /// the stream open behind the provider's own timeout.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Deadline for one whole tool invocation, in seconds. Default: 30.
    /// </summary>
    /// <remarks>
    /// Bounds name resolution and the statement together, so it must be at least
    /// <see cref="TimeoutSeconds"/> — a call deadline shorter than its own statement's timeout would
    /// abandon every query before that timeout could report one, which reads as a broken tool rather
    /// than a slow database. Startup validation enforces it.
    /// </remarks>
    public int ToolTimeoutSeconds { get; set; } = 30;
}

/// <summary>Validates <see cref="SheetQueryOptions"/>.</summary>
public sealed class SheetQueryOptionsValidator : AbstractValidator<SheetQueryOptions>
{
    public SheetQueryOptionsValidator()
    {
        RuleFor(options => options.MaxResultRows).InclusiveBetween(1, 500)
            .WithMessage("SheetQuery:MaxResultRows must be between 1 and 500.");
        RuleFor(options => options.MaxResultCharacters).InclusiveBetween(1_000, 200_000)
            .WithMessage("SheetQuery:MaxResultCharacters must be between 1000 and 200000.");
        RuleFor(options => options.MaxGroups).InclusiveBetween(1, 2_000)
            .WithMessage("SheetQuery:MaxGroups must be between 1 and 2000.");
        RuleFor(options => options.MaxFilters).InclusiveBetween(1, 20)
            .WithMessage("SheetQuery:MaxFilters must be between 1 and 20.");
        RuleFor(options => options.TimeoutSeconds).InclusiveBetween(1, 120)
            .WithMessage("SheetQuery:TimeoutSeconds must be between 1 and 120.");
        RuleFor(options => options.ToolTimeoutSeconds).InclusiveBetween(1, 300)
            .WithMessage("SheetQuery:ToolTimeoutSeconds must be between 1 and 300.");
        // A call deadline shorter than the statement's own timeout would abandon every query first.
        RuleFor(options => options.ToolTimeoutSeconds).GreaterThanOrEqualTo(options => options.TimeoutSeconds)
            .WithMessage("SheetQuery:ToolTimeoutSeconds must be at least SheetQuery:TimeoutSeconds.");
    }
}
