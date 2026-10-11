using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <remarks>
/// A sheet past a ceiling is refused, not truncated: a silently shortened sheet makes every later answer
/// about it quietly wrong. The permitted ranges are Excel's own grid limits.
/// </remarks>
public sealed class SheetOptions
{
    public const string SectionName = "Sheets";

    public int MaxRowsPerSheet { get; set; } = 20_000;

    /// <summary>
    /// Across every sheet: workbooks have no sheet limit, so the per-sheet ceiling alone would let a hundred
    /// narrow sheets become millions of rows in one save.
    /// </summary>
    public int MaxRowsPerUpload { get; set; } = 50_000;

    public int MaxColumnsPerSheet { get; set; } = 200;

    /// <summary>
    /// Cell text per upload, shared strings included. Grid ceilings do not bound text, and a compressed
    /// workbook expands a thousandfold, so this bounds host cost and how far a package may inflate.
    /// </summary>
    public int MaxCharactersPerUpload { get; set; } = 8_000_000;

    public RowWindowOptions RowWindow { get; set; } = new();
}

public sealed class SheetOptionsValidator : AbstractValidator<SheetOptions>
{
    public SheetOptionsValidator()
    {
        RuleFor(options => options.MaxRowsPerSheet).InclusiveBetween(1, 1_048_576)
            .WithMessage("Sheets:MaxRowsPerSheet must be between 1 and 1048576.");
        RuleFor(options => options.MaxRowsPerUpload).InclusiveBetween(1, 1_048_576)
            .WithMessage("Sheets:MaxRowsPerUpload must be between 1 and 1048576.");
        RuleFor(options => options.MaxColumnsPerSheet).InclusiveBetween(1, 16_384)
            .WithMessage("Sheets:MaxColumnsPerSheet must be between 1 and 16384.");
        RuleFor(options => options.MaxCharactersPerUpload).InclusiveBetween(1_024, 512_000_000)
            .WithMessage("Sheets:MaxCharactersPerUpload must be between 1024 and 512000000.");
        RuleFor(options => options.MaxRowsPerUpload).GreaterThanOrEqualTo(options => options.MaxRowsPerSheet)
            .WithMessage("Sheets:MaxRowsPerUpload must be at least Sheets:MaxRowsPerSheet.");
        RuleFor(options => options.RowWindow).SetValidator(new RowWindowOptionsValidator());
    }
}
