using Azure.Identity;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Registers the Microsoft Graph client used to provision users.
/// </summary>
internal static class GraphProviderConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the client, authenticated as this application with its client secret.
    /// </summary>
    internal static IServiceCollection AddGraphProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AzureAdOptions>()
            .Bind(configuration.GetSection(AzureAdOptions.SectionName));

        services.AddSingleton(services =>
        {
            var azureAd = services.GetRequiredService<IOptions<AzureAdOptions>>().Value;

            return new GraphServiceClient(new ClientSecretCredential(azureAd.TenantId, azureAd.ClientId, azureAd.ClientSecret));
        });

        return services;
    }

    #endregion
}
