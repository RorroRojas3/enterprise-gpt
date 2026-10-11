using Enterprise.Gpt.Service.Documents.Sheets;
using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// Separate from <c>Documents:Retrieval</c>'s caps: this tool returns structured rows and scans whole sheets,
/// so neither its size nor its time budget follows from retrieval's.
/// </remarks>
public sealed class SheetQueryOptions
{
    public const string SectionName = "SheetQuery";

    /// <summary>
    /// Off by default. Read through <see cref="ISheetQueryOptionsProvider"/> so a flip reaches the next turn
    /// on configuration reload; spreadsheet ingestion is unaffected.
    /// </summary>
    public bool Enabled { get; set; }

    public int MaxResultRows { get; set; } = 50;

    /// <summary>Bounds result width, which the row cap alone does not.</summary>
    public int MaxResultCharacters { get; set; } = 20_000;

    public int MaxGroups { get; set; } = 200;

    /// <summary>
    /// Refused past this, not trimmed: a dropped filter would return rows the model believes it excluded.
    /// </summary>
    public int MaxFilters { get; set; } = 5;

    /// <summary>Per statement; short because the tool runs inside a live chat turn.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Deadline for the whole invocation, name resolution and statement together.</summary>
    public int ToolTimeoutSeconds { get; set; } = 30;
}

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
