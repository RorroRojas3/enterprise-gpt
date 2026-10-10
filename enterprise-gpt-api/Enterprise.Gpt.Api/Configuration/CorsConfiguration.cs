namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers and applies the CORS policy for the web client's origins.
/// </summary>
internal static class CorsConfiguration
{
    private const string PolicyName = "AllowSpecificOrigins";

    /// <summary>
    /// Allows the origins listed under <c>CorsOrigins</c>, with credentials.
    /// </summary>
    internal static IServiceCollection AddEnterpriseGptCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("CorsOrigins").Get<string[]>() ?? [];

        services.AddCors(cors => cors.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            // Request-Context is exposed, not merely allowed: without it the browser SDK silently skips resolving
            // which Application Insights resource a dependency reached, and reports the failure nowhere.
            .WithExposedHeaders("Content-Disposition", "Request-Context")));

        return services;
    }

    /// <summary>
    /// Applies the policy registered by <see cref="AddEnterpriseGptCors"/>.
    /// </summary>
    internal static IApplicationBuilder UseEnterpriseGptCors(this IApplicationBuilder app) => app.UseCors(PolicyName);
}
