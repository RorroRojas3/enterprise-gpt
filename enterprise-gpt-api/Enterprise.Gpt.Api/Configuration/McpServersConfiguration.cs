using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.McpServers;
using Enterprise.Gpt.Service.Options;
using FluentValidation;

namespace Enterprise.Gpt.Api.Configuration;

/// <summary>
/// Registers MCP servers: the catalog, user credentials, and the client cache tools are leased from.
/// </summary>
internal static class McpServersConfiguration
{
    /// <summary>
    /// Registers the MCP services, the singleton connection cache and the per-request tool provider.
    /// </summary>
    internal static IServiceCollection AddMcpServers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<McpClientCacheOptions>, McpClientCacheOptionsValidator>();
        services.AddOptions<McpClientCacheOptions>()
            .Bind(configuration.GetSection(McpClientCacheOptions.SectionName))
            .ValidateWithFluentValidation()
            .ValidateOnStart();

        services.AddSingleton<IMcpClientCache, McpClientCache>();
        services.AddScoped<IMcpToolProvider, McpToolProvider>();
        // No timeout: MCP sessions stream over SSE. Connecting is bounded by McpClientCacheOptions.ConnectionTimeout.
        services.AddHttpClient(McpToolProvider.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddScoped<IMcpServerService, McpServerService>();
        services.AddScoped<IUserMcpCredentialService, UserMcpCredentialService>();

        return services;
    }
}
