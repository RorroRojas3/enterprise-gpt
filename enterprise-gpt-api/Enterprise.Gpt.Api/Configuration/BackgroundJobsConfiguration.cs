using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the background job pipeline that ingests uploaded documents.
/// </summary>
internal static class BackgroundJobsConfiguration
{
    #region Public static methods

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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
        services.AddSingleton<IJobStatusStore, JobStatusStore>();
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();
        services.AddHostedService<BackgroundJobProcessor>();

        return services;
    }

    #endregion
}
