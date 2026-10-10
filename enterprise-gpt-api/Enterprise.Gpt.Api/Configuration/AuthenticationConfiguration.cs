using Enterprise.Gpt.Api.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers Entra ID bearer authentication and on-behalf-of token acquisition.
/// </summary>
internal static class AuthenticationConfiguration
{
    /// <summary>
    /// Validates incoming tokens against the <c>AzureAd</c> section and caches downstream tokens in memory.
    /// </summary>
    internal static IServiceCollection AddEnterpriseGptAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(
                options =>
                {
                    configuration.Bind(AzureAdOptions.SectionName, options);

                    // Explicit, so a token issued for another application is rejected.
                    var clientId = configuration[$"{AzureAdOptions.SectionName}:{nameof(AzureAdOptions.ClientId)}"];
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ValidAudiences = [clientId, $"api://{clientId}"];
                },
                options => configuration.Bind(AzureAdOptions.SectionName, options))
            .EnableTokenAcquisitionToCallDownstreamApi(options => configuration.Bind(AzureAdOptions.SectionName, options))
            .AddInMemoryTokenCaches();

        return services;
    }
}
