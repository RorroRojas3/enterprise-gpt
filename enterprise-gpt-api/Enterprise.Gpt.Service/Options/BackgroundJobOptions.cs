using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>
/// Settings for the background job pipeline, bound from the <c>BackgroundJobs</c> configuration section.
/// </summary>
public sealed class BackgroundJobOptions
{
    /// <summary>The configuration section this type binds from.</summary>
    public const string SectionName = "BackgroundJobs";

    /// <summary>
    /// How many jobs run at once; zero or less means twice the processor count. Default: 0.
    /// </summary>
    public int MaxConcurrent { get; set; }

    /// <summary>
    /// How long a finished job's status stays readable, in minutes. Default: 60.
    /// </summary>
    public int RetentionMinutes { get; set; } = 60;
}

/// <summary>Validates <see cref="BackgroundJobOptions"/>.</summary>
public sealed class BackgroundJobOptionsValidator : AbstractValidator<BackgroundJobOptions>
{
    public BackgroundJobOptionsValidator()
    {
        RuleFor(options => options.RetentionMinutes).GreaterThan(0)
            .WithMessage("BackgroundJobs:RetentionMinutes must be greater than zero.");
    }
}
