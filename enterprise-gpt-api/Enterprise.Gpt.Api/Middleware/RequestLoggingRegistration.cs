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
    internal static IServiceCollection AddRequestLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestLoggingOptions>()
            .Bind(configuration.GetSection(RequestLoggingOptions.SectionName))
            // After binding, so a list the operator did configure replaces the default instead of
            // extending it — see the remarks on RequestLoggingOptions.
            .PostConfigure(options => options.ApplyListDefaults())
            .ValidateDataAnnotations()
            .Validate(
                options => options.SlowRequestThreshold > TimeSpan.Zero,
                "RequestLogging:SlowRequestThreshold must be greater than zero.")
            .Validate(
                options => Enum.IsDefined(options.RequestStartLevel),
                "RequestLogging:RequestStartLevel must be a valid log level.")
            .Validate(
                options => Enum.IsDefined(options.Bodies.LogLevel),
                "RequestLogging:Bodies:LogLevel must be a valid log level.")
            // Spelled out rather than left to the [Range] on the property: ValidateDataAnnotations only
            // inspects the root object, so an attribute on a nested options type is documentation only.
            .Validate(
                options => options.Bodies.MaxBodyBytes is > 0 and <= 1024 * 1024,
                "RequestLogging:Bodies:MaxBodyBytes must be between 1 and 1048576.")
            .Validate(
                options => options.HasValidEntries(),
                "RequestLogging list entries must be non-empty, and every path must start with '/'.")
            // Validated at startup because the alternative diagnostics are all worse. A non-positive
            // threshold silently marks every request slow; an unrooted path or a null list entry throws
            // from the middleware's own field initialisers, which run once when the pipeline is built and
            // surface as a container activation failure rather than as a message naming the setting.
            .ValidateOnStart();

        // TryAdd so an application that registers its own redactor first keeps it.
        services.TryAddSingleton<IRequestBodyRedactor, JsonPropertyRedactor>();

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
    internal static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

        return options.Enabled ? app.UseMiddleware<RequestLoggingMiddleware>() : app;
    }
}
