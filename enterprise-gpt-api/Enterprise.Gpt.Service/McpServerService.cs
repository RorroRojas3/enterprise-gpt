using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using RR.AI_Chat.Dto;
using RR.AI_Chat.Service.Settings;
using System.Net.Http.Headers;

namespace RR.AI_Chat.Service
{
    public interface IMcpServerService
    {
        /// <summary>
        /// Creates and configures an MCP client for the specified server name using the current user's access token.
        /// </summary>
        /// <param name="name">The case-insensitive name of the configured MCP server.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the client creation.</param>
        /// <returns>A configured <see cref="McpClient"/> instance for the target server.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no MCP server with the given <paramref name="name"/> is found in configuration.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        /// <remarks>
        /// The client is created with an HTTP transport pointing to the server's URI and authorized with a bearer token
        /// acquired for the server's configured scope. Exceptions from token acquisition or HTTP/MCP operations may propagate.
        /// </remarks>
        Task<McpClient> CreateClientAsync(string name, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves the list of tools exposed by the connected MCP server.
        /// </summary>
        /// <param name="mcpClient">An initialized <see cref="McpClient"/> connected to the target server.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>A list of tools available on the server.</returns>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        /// <remarks>
        /// This delegates to <see cref="McpClient.ListToolsAsync"/> and returns its results.
        /// </remarks>
        Task<IList<McpClientTool>> GetToolsFromServerAsync(McpClient mcpClient, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the MCP servers configured for the application.
        /// </summary>
        /// <returns>A list of MCP server descriptors containing the server names.</returns>
        List<McpDto> GetMcpServers();
    }

    public class McpServerService(ILogger<McpServerService> logger,
        ITokenAcquisition tokenAcquisition,
        IOptions<List<McpServerSettings>> mcpServerSettings,
        IHttpClientFactory httpClientFactory) : IMcpServerService
    {
        private readonly ILogger<McpServerService> _logger = logger;
        private readonly ITokenAcquisition _tokenAcquisition = tokenAcquisition;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly List<McpServerSettings> _mcpServerSettings = mcpServerSettings.Value;

        /// <inheritdoc />
        public async Task<McpClient> CreateClientAsync(string name, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var mcpServer = _mcpServerSettings.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (mcpServer == null)
            {
                _logger.LogError("MCP server with name {name} not found in configuration.", name);
                throw new InvalidOperationException($"MCP server with name {name} not found in configuration.");
            }

            var scopes = new[] { mcpServer.Scope };
            var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.BaseAddress = mcpServer.Uri;

            var transport = new HttpClientTransport(new()
            {
                Endpoint = mcpServer.Uri,
                Name = mcpServer.Name,
            }, httpClient);

            var mcpClient = await McpClient.CreateAsync(transport, null, null, cancellationToken).ConfigureAwait(false);

            return mcpClient;
        }

        /// <inheritdoc />
        public async Task<IList<McpClientTool>> GetToolsFromServerAsync(McpClient mcpClient, CancellationToken cancellationToken)
        {
            var tools = await mcpClient.ListToolsAsync(new RequestOptions(), cancellationToken).ConfigureAwait(false);
            return tools;
        }

        /// <inheritdoc />
        public List<McpDto> GetMcpServers()
        {
            return [.. _mcpServerSettings.Select(s => new McpDto
            {
                Name = s.Name
            })];
        }
    }
}
