using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Reports;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

internal static class ReportsConfiguration
{
    #region Public static methods

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
