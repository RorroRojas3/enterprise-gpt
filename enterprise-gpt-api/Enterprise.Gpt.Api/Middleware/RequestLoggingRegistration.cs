using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Registers <see cref="RequestLoggingMiddleware"/> and the services it depends on.
/// </summary>
internal static class RequestLoggingRegistration
{
    /// <summary>
    /// Binds and validates <see cref="RequestLoggingOptions"/> and registers the default body redactor.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">Supplies the <c>RequestLogging</c> section.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    internal static IServiceCollection AddEnterpriseGptRequestLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<RequestLoggingOptions>, RequestLoggingOptionsValidator>();
        services.AddOptions<RequestLoggingOptions>()
            .Bind(configuration.GetSection(RequestLoggingOptions.SectionName))
            // After binding, so a list the operator did configure replaces the default instead of
            // extending it — see the remarks on RequestLoggingOptions.
            .PostConfigure(options => options.ApplyListDefaults())
            .ValidateWithFluentValidation()
            // Validated at startup because the alternative diagnostics are all worse. A non-positive
            // threshold silently marks every request slow; an unrooted path or a null list entry throws
            // from the middleware's own field initialisers, which run once when the pipeline is built and
            // surface as a container activation failure rather than as a message naming the setting.
            .ValidateOnStart();

        // TryAdd so an application that registers its own redactor first keeps it.
        services.TryAddSingleton<IRequestBodyRedactor, JsonPropertyRedactor>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Adds <see cref="RequestLoggingMiddleware"/> to the pipeline unless it is disabled by configuration.
    /// </summary>
    /// <param name="app">The application pipeline to add to.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    /// <remarks>
    /// A disabled access log is left out of the pipeline rather than short-circuited inside the
    /// middleware, so it costs nothing per request. The trade-off is that the switch is read once, at
    /// startup.
    /// </remarks>
    internal static IApplicationBuilder UseEnterpriseGptRequestLogging(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

        return options.Enabled ? app.UseMiddleware<RequestLoggingMiddleware>() : app;
    }
}
