using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the background job pipeline that ingests uploaded documents.
/// </summary>
internal static class BackgroundJobsConfiguration
{
    /// <summary>
    /// Registers the queue, the status store clients poll, the cancellation registry and the processor.
    /// </summary>
    internal static IServiceCollection AddEnterpriseGptBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<BackgroundJobOptions>, BackgroundJobOptionsValidator>();
        services.AddOptions<BackgroundJobOptions>()
            .Bind(configuration.GetSection(BackgroundJobOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
        services.AddSingleton<IJobStatusStore, JobStatusStore>();
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();
        services.AddHostedService<BackgroundJobProcessor>();

        return services;
    }
}
