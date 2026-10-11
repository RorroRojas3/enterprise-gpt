using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Middleware;

internal static class RequestLoggingRegistration
{
    #region Public static methods

    internal static IServiceCollection AddEnterpriseGptRequestLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<RequestLoggingOptions>, RequestLoggingOptionsValidator>();
        services.AddOptions<RequestLoggingOptions>()
            .Bind(configuration.GetSection(RequestLoggingOptions.SectionName))
            // After binding, so a list the operator did configure replaces the default instead of
            // extending it — see the remarks on RequestLoggingOptions.
            .PostConfigure(options => options.ApplyListDefaults())
            .ValidateWithFluentValidation()
            // A non-positive threshold silently marks every request slow, and a bad path or list entry would
            // throw from the middleware's field initialisers as an activation failure naming no setting.
            .ValidateOnStart();

        // TryAdd so an application that registers its own redactor first keeps it.
        services.TryAddSingleton<IRequestBodyRedactor, JsonPropertyRedactor>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <remarks>
    /// Left out of the pipeline when disabled, so it costs nothing per request; the switch is read once.
    /// </remarks>
    internal static IApplicationBuilder UseEnterpriseGptRequestLogging(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

        return options.Enabled ? app.UseMiddleware<RequestLoggingMiddleware>() : app;
    }

    #endregion
}
