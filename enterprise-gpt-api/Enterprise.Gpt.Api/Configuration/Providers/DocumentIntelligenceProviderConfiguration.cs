using Azure;
using Azure.AI.DocumentIntelligence;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Configuration.Providers;

internal static class DocumentIntelligenceProviderConfiguration
{
    #region Public static methods

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
