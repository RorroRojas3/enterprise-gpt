using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

/// <summary>Sizing for the header-repeating row windows a sheet is emitted as.</summary>
public sealed class RowWindowOptions
{
    /// <summary>
    /// Share of the chunker's budget a window aims for: under it, so the window stays one chunk with its
    /// header instead of spilling into bare rows.
    /// </summary>
    public double TargetTokenFraction { get; set; } = 0.75;

    /// <summary>Data rows per window, whatever the token count.</summary>
    public int MaxRows { get; set; } = 50;
}

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
