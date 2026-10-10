using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Sizing for the header-repeating row windows a sheet is emitted as, bound from the
/// <c>Sheets:RowWindow</c> configuration section.
/// </summary>
public sealed class RowWindowOptions
{
    /// <summary>
    /// Share of the chunker's own token budget a window aims for. Default: 0.75.
    /// </summary>
    /// <remarks>
    /// Comfortably under the budget so a window survives chunking as one indivisible unit and keeps
    /// its header; at the budget it would regularly spill and degrade to bare rows.
    /// </remarks>
    public double TargetTokenFraction { get; set; } = 0.75;

    /// <summary>
    /// Largest number of data rows in one window, whatever the token count. Default: 50.
    /// </summary>
    public int MaxRows { get; set; } = 50;
}

/// <summary>Validates <see cref="RowWindowOptions"/>.</summary>
public sealed class RowWindowOptionsValidator : AbstractValidator<RowWindowOptions>
{
    public RowWindowOptionsValidator()
    {
        RuleFor(options => options.TargetTokenFraction).InclusiveBetween(0.1d, 1d)
            .WithMessage("Sheets:RowWindow:TargetTokenFraction must be between 0.1 and 1.");
        RuleFor(options => options.MaxRows).InclusiveBetween(1, 1_000)
            .WithMessage("Sheets:RowWindow:MaxRows must be between 1 and 1,000.");
    }
}
