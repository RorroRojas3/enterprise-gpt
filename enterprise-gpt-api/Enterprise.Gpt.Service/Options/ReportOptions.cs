using FluentValidation;


namespace Enterprise.Gpt.Service.Options;

public sealed class ReportOptions
{
    public const string SectionName = "Reports";

    /// <summary>
    /// Longest window <c>GET api/reports/usage</c> aggregates; it bounds a scan of an unpruned table.
    /// </summary>
    /// <remarks>Longer ranges are refused, not clamped, so a caller never misreads a shorter answer.</remarks>
    public int MaxRangeDays { get; set; } = 366;

    /// <summary>
    /// Groups per report before the breakdown is cut, reachable only when grouping by user. Period totals
    /// cover the whole window regardless, so truncation never makes the headline figures wrong.
    /// </summary>
    public int MaxGroups { get; set; } = 500;
}

public sealed class ReportOptionsValidator : AbstractValidator<ReportOptions>
{
    public ReportOptionsValidator()
    {
        RuleFor(options => options.MaxRangeDays).InclusiveBetween(1, 3660)
            .WithMessage("Reports:MaxRangeDays must be between 1 and 3660.");
        RuleFor(options => options.MaxGroups).InclusiveBetween(1, 5000)
            .WithMessage("Reports:MaxGroups must be between 1 and 5000.");
    }
}
