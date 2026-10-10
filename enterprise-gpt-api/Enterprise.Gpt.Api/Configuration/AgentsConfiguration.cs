using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers the File Agent.
/// </summary>
internal static class AgentsConfiguration
{
    /// <summary>
    /// Registers the File Agent's options, its collaborators and the conversion matrix.
    /// </summary>
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

        // Read once and shared: the file ships with the build, and the pre-flight that refuses a conversion
        // consults it on every run. The startup validator forces construction, so a matrix missing from the
        // build fails the deploy rather than the first request.
        services.AddSingleton<IConversionMatrix>(_ => ConversionMatrix.Load());

        return services;
    }
}
