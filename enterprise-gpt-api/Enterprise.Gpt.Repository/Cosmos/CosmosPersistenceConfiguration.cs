using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Repository.Cosmos.HealthChecks;
using Enterprise.Gpt.Repository.Cosmos.Options;
using Enterprise.Gpt.Repository.Cosmos.Provisioning;
using Enterprise.Gpt.Repository.Cosmos.Serialization;
using FluentValidation;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Repository.Cosmos;

/// <summary>
/// Registers the Cosmos DB store that holds conversation transcripts.
/// </summary>
public static class CosmosPersistenceConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the validated Cosmos options, the account client, the transcript store and its provisioner.
    /// </summary>
    public static IServiceCollection AddEnterpriseGptCosmosPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidator<CosmosDbOptions>, CosmosDbOptionsValidator>();
        services.AddOptions<CosmosDbOptions>()
            .Bind(configuration.GetSection(CosmosDbOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        // System.Text.Json rather than the SDK's Newtonsoft default: the document types carry
        // [JsonPropertyName] attributes that the default serializer never reads.
        services.AddSingleton(services => new CosmosClient(
            services.GetRequiredService<IOptions<CosmosDbOptions>>().Value.ConnectionString,
            new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateOptions() }));
        services.AddSingleton<ITranscriptStore, CosmosTranscriptStore>();
        services.AddSingleton<CosmosResourceProvisioner>();

        return services;
    }

    /// <summary>
    /// Adds the probe that reports whether the transcript account is reachable.
    /// </summary>
    public static IHealthChecksBuilder AddEnterpriseGptCosmosHealthCheck(this IHealthChecksBuilder builder, string name, IEnumerable<string> tags) =>
        builder.AddCheck<CosmosHealthCheck>(name, tags: tags);

    #endregion
}
