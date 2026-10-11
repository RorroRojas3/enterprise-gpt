using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>Zero or less means twice the processor count.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>How long a finished job's status stays readable.</summary>
    public int RetentionMinutes { get; set; } = 60;
}

public sealed class BackgroundJobOptionsValidator : AbstractValidator<BackgroundJobOptions>
{
    public BackgroundJobOptionsValidator()
    {
        RuleFor(options => options.RetentionMinutes).GreaterThan(0)
            .WithMessage("BackgroundJobs:RetentionMinutes must be greater than zero.");
    }
}
