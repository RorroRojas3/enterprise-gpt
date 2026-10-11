using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

internal static class AgentsConfiguration
{
    #region Public static methods

    internal static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<FileAgentOptions>, FileAgentOptionsValidator>();
        services.AddOptions<FileAgentOptions>()
            .Bind(configuration.GetSection(FileAgentOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddScoped<IFileAgentModelResolver, FileAgentModelResolver>();
        services.AddScoped<IFileAgentDocumentReader, FileAgentDocumentReader>();
        services.AddScoped<IFileAgentQuotaService, FileAgentQuotaService>();
        services.AddScoped<IFileAgentToolProvider, FileAgentToolProvider>();
        services.AddSingleton<IGeneratedArtifactVerifier, GeneratedArtifactVerifier>();

        // Shared: the pre-flight consults it on every run. The startup validator forces construction, so a
        // matrix missing from the build fails the deploy rather than the first request.
        services.AddSingleton<IConversionMatrix>(_ => ConversionMatrix.Load());

        return services;
    }

    #endregion
}
