namespace Enterprise.Gpt.Api.Configuration;

internal static class CorsConfiguration
{
    private const string PolicyName = "AllowSpecificOrigins";

    #region Public static methods

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

    internal static IApplicationBuilder UseEnterpriseGptCors(this IApplicationBuilder app) => app.UseCors(PolicyName);

    #endregion
}
