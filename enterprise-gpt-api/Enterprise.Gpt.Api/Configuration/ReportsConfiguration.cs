using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Reports;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers usage reporting.
/// </summary>
internal static class ReportsConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the report service and its range and grouping bounds.
    /// </summary>
    internal static IServiceCollection AddReports(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<ReportOptions>, ReportOptionsValidator>();
        services.AddOptions<ReportOptions>()
            .Bind(configuration.GetSection(ReportOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddScoped<IReportService, ReportService>();

        return services;
    }

    #endregion
}
