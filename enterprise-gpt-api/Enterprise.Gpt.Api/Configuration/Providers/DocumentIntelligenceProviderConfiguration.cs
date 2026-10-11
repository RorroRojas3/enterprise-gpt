using Azure;
using Azure.AI.DocumentIntelligence;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// Registers the Azure Document Intelligence client that OCRs uploaded documents.
/// </summary>
internal static class DocumentIntelligenceProviderConfiguration
{
    #region Public static methods

    /// <summary>
    /// Registers the validated options and the client, which is built on first use.
    /// </summary>
    internal static IServiceCollection AddDocumentIntelligenceProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<DocumentIntelligenceOptions>, DocumentIntelligenceOptionsValidator>();
        services.AddOptions<DocumentIntelligenceOptions>()
            .Bind(configuration.GetSection(DocumentIntelligenceOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddSingleton(services =>
        {
            var settings = services.GetRequiredService<IOptions<DocumentIntelligenceOptions>>().Value;

            return new DocumentIntelligenceClient(new Uri(settings.Endpoint), new AzureKeyCredential(settings.ApiKey));
        });

        return services;
    }

    #endregion
}
