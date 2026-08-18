using Andes.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using System.Net.Sockets;

namespace Enterprise.Gpt.Service
{
    /// <summary>
    /// Identifies an MCP server whose tools were leased for a request, captured as the server
    /// stood at that moment.
    /// </summary>
    /// <param name="Id">The server's unique identifier.</param>
    /// <param name="Name">The server's name at lease time.</param>
    public readonly record struct McpServerReference(Guid Id, string Name);

    /// <summary>
    /// A set of leased MCP tools spanning every server a chat request selected. Dispose it
    /// when the stream ends (successfully or not) to release the underlying clients back to
    /// the cache.
    /// </summary>
    public interface IMcpToolLeaseSet : IAsyncDisposable
    {
        /// <summary>
        /// Gets the tools from every resolved server, ready to assign to
        /// <see cref="ChatOptions.Tools"/>.
        /// </summary>
        IReadOnlyList<AITool> Tools { get; }

        /// <summary>
        /// Gets the servers behind those tools. Surfaced here rather than looked up again by the
        /// caller: resolution already loaded these rows, and a server renamed between the lease and
        /// the audit write would otherwise be recorded under its new name.
        /// </summary>
        IReadOnlyList<McpServerReference> Servers { get; }
    }

    public interface IMcpToolProvider
    {
        /// <summary>
        /// Resolves the selected MCP servers — each must exist, be active, and be permitted
        /// for the calling user — and obtains their tools through the client cache, connecting
        /// and listing in parallel on cache misses.
        /// </summary>
        /// <param name="mcpServerIds">The ids of the selected servers; duplicates are ignored.</param>
        /// <param name="cancellationToken">A token that propagates cancellation.</param>
        /// <returns>A lease set the caller must dispose when the stream completes.</returns>
        /// <exception cref="NotFoundException">A selected server does not exist or is deactivated.</exception>
        /// <exception cref="ForbiddenException">The user lacks the permission for a selected server.</exception>
        /// <exception cref="McpAuthorizationRequiredException">Token acquisition requires user consent or additional authentication.</exception>
        /// <exception cref="McpServerUnavailableException">Connecting to a server or listing its tools failed.</exception>
        Task<IMcpToolLeaseSet> AcquireToolsAsync(IReadOnlyCollection<Guid> mcpServerIds, CancellationToken cancellationToken);
    }

    public class McpToolProvider(ILogger<McpToolProvider> logger,
        ITokenService tokenService,
        ITokenAcquisition tokenAcquisition,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IOptions<McpClientCacheOptions> options,
        IMcpClientCache mcpClientCache,
        EnterpriseGptDbContext ctx) : IMcpToolProvider
    {
        /// <summary>
        /// Name of the <see cref="IHttpClientFactory"/> client used for MCP connections;
        /// registered with an infinite timeout because MCP sessions stream over SSE.
        /// </summary>
        public const string HttpClientName = "Mcp";

        private readonly ILogger<McpToolProvider> _logger = logger;
        private readonly ITokenService _tokenService = tokenService;
        private readonly ITokenAcquisition _tokenAcquisition = tokenAcquisition;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly ILoggerFactory _loggerFactory = loggerFactory;
        private readonly McpClientCacheOptions _options = options.Value;
        private readonly IMcpClientCache _mcpClientCache = mcpClientCache;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        /// <inheritdoc />
        public async Task<IMcpToolLeaseSet> AcquireToolsAsync(IReadOnlyCollection<Guid> mcpServerIds, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(mcpServerIds);

            var ids = mcpServerIds.Distinct().ToList();
            if (ids.Count == 0)
            {
                return McpToolLeaseSet.Empty;
            }

            var oid = _tokenService.GetOid();

            // One query resolves existence and authorization for every selected server. This check
            // deliberately reads the database rather than IUserPermissionCache: a tool call runs
            // code on the user's behalf against a third-party server, so a revoked grant must take
            // effect on the next request rather than when a cache entry expires. The query is
            // negligible next to establishing the MCP session that follows it.
            var servers = await _ctx.McpServers
                .AsNoTracking()
                .Where(s => ids.Contains(s.Id) && !s.DateDeactivated.HasValue)
                .Select(s => new
                {
                    Server = s,
                    IsPermitted = s.Permissions.Any(p => !p.DateDeactivated.HasValue
                        && p.UserPermissions.Any(up => up.UserId == oid && !up.DateDeactivated.HasValue))
                })
                .ToListAsync(cancellationToken);

            if (servers.Count < ids.Count)
            {
                var foundIds = servers.Select(x => x.Server.Id).ToHashSet();
                var missingId = ids.First(id => !foundIds.Contains(id));
                throw new NotFoundException($"MCP server {missingId} not found.");
            }

            var denied = servers.FirstOrDefault(x => !x.IsPermitted);
            if (denied is not null)
            {
                throw new ForbiddenException($"You do not have permission to use MCP server '{denied.Server.Name}'.");
            }

            var leaseTasks = servers.Select(x => AcquireLeaseAsync(x.Server, oid, cancellationToken)).ToArray();
            try
            {
                var leases = await Task.WhenAll(leaseTasks);
                return new McpToolLeaseSet(leases, [.. servers.Select(x => new McpServerReference(x.Server.Id, x.Server.Name))]);
            }
            catch
            {
                // WhenAll completes only after every task has finished, so siblings that
                // succeeded can be released here without racing their acquisition.
                foreach (var task in leaseTasks.Where(t => t.IsCompletedSuccessfully))
                {
                    var lease = await task;
                    await lease.DisposeAsync();
                }

                throw;
            }
        }

        private async Task<IMcpToolLease> AcquireLeaseAsync(McpServer server, Guid oid, CancellationToken cancellationToken)
        {
            if (server.AuthType == McpAuthTypes.EntraIdOnBehalfOf)
            {
                // The token is acquired here — in scoped code with the user's assertion —
                // rather than inside the cache factory, so the singleton cache never touches
                // scoped services. MSAL's token cache makes repeat acquisitions cheap, and an
                // expired cached entry transparently gets a fresh token on recreation.
                var (accessToken, expiresOn) = await AcquireAccessTokenAsync(server);

                return await _mcpClientCache.GetOrCreateAsync(
                    McpCacheKey.ForUser(server.Id, oid),
                    token => CreateEntrySourceAsync(server, accessToken, expiresOn, token),
                    cancellationToken);
            }

            // Servers without authentication share one entry across users: no token is
            // involved and the tool list is identical for everyone.
            return await _mcpClientCache.GetOrCreateAsync(
                McpCacheKey.Shared(server.Id),
                token => CreateEntrySourceAsync(server, accessToken: null, tokenExpiresOn: null, token),
                cancellationToken);
        }

        private async Task<(string AccessToken, DateTimeOffset ExpiresOn)> AcquireAccessTokenAsync(McpServer server)
        {
            try
            {
                var result = await _tokenAcquisition.GetAuthenticationResultForUserAsync([server.Scope!]);
                return (result.AccessToken, result.ExpiresOn);
            }
            catch (MicrosoftIdentityWebChallengeUserException ex)
            {
                throw new McpAuthorizationRequiredException(server.Name, ex);
            }
            catch (MsalUiRequiredException ex)
            {
                throw new McpAuthorizationRequiredException(server.Name, ex);
            }
            catch (MsalException ex)
            {
                throw new McpServerUnavailableException(server.Name, ex);
            }
        }

        private async Task<McpCacheEntrySource> CreateEntrySourceAsync(
            McpServer server, string? accessToken, DateTimeOffset? tokenExpiresOn, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url),
                Name = server.Name,
                ConnectionTimeout = _options.ConnectionTimeout
            };
            if (accessToken is not null)
            {
                transportOptions.AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {accessToken}"
                };
            }

            var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory, ownsHttpClient: true);

            McpClient client;
            try
            {
                client = await McpClient.CreateAsync(transport, null, _loggerFactory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectivityFailure(ex, cancellationToken))
            {
                await DisposeTransportAsync(transport).ConfigureAwait(false);
                throw new McpServerUnavailableException(server.Name, ex);
            }
            catch
            {
                await DisposeTransportAsync(transport).ConfigureAwait(false);
                throw;
            }

            try
            {
                var tools = await client.ListToolsAsync(new RequestOptions(), cancellationToken).ConfigureAwait(false);

                // Prefixing with the server name keeps tool names unique when several servers
                // expose a tool with the same name; the SDK still calls the original name.
                //
                // WithTracking is what makes these recognizable to the tool-tracking middleware: it
                // carries the server identity onto the wrapper, so an invocation is reported as an
                // MCP call attributed to this server rather than as an anonymous function, and it
                // bridges the server's progress notifications into the streamed status feed. The
                // renaming happens first so the tracked wrapper preserves the prefixed name.
                //
                // The catalog name is passed explicitly rather than letting the overload read the
                // server's self-advertised ServerInfo name, because that name and this prefix would
                // then be two different strings. Every consumer that has to undo the prefix — the
                // client, which strips it to label a card with the tool that ran, and
                // UsageReportTranslator's fallback, which matches a reported source back to a
                // catalog row — needs the name the prefix was built from. It is also the name the
                // user picked in the server selector, which is what they expect to see beside the
                // tool. enableProgress is stated rather than defaulted: it is the only reason the
                // sub-status lines exist, and a silent default flip would remove them with nothing
                // failing.
                var prefix = SanitizeToolNamePrefix(server.Name);
                List<AITool> prefixedTools =
                [
                    .. tools.Select(t => t.WithName($"{prefix}_{t.Name}").WithTracking(server.Name, enableProgress: true))
                ];

                _logger.LogInformation("Connected to MCP server {McpServerId} and listed {ToolCount} tools", server.Id, prefixedTools.Count);

                return new McpCacheEntrySource
                {
                    Client = client,
                    Tools = prefixedTools,
                    TokenExpiresOn = tokenExpiresOn
                };
            }
            catch (Exception ex) when (IsConnectivityFailure(ex, cancellationToken))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new McpServerUnavailableException(server.Name, ex);
            }
            catch
            {
                // Cancellation or an unexpected failure: the connection must not leak either way.
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Classifies connect/list failures that should surface as 502 "server unavailable".
        /// A timeout raised by the transport's own connection timeout arrives as a cancellation
        /// that does not match the caller's token, so it counts as a connectivity failure.
        /// </summary>
        private static bool IsConnectivityFailure(Exception exception, CancellationToken cancellationToken)
        {
            return exception switch
            {
                OperationCanceledException => !cancellationToken.IsCancellationRequested,
                HttpRequestException or McpException or TimeoutException or IOException or SocketException => true,
                _ => false
            };
        }

        private static async Task DisposeTransportAsync(HttpClientTransport transport)
        {
            // The transport owns the HttpClient; make sure a failed connect doesn't leak it.
            if (transport is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Derives the prefix stamped onto every tool leased from a server, so tool names stay
        /// unique across servers.
        /// </summary>
        /// <param name="serverName">The server's name at lease time.</param>
        /// <returns>The prefix, with everything outside <c>[A-Za-z0-9_-]</c> replaced by an underscore.</returns>
        /// <remarks>
        /// Internal rather than private because it is also the only reliable way to attribute a
        /// reported tool call back to its server: usage reports carry the tool name the model
        /// called, which is <c>{prefix}_{tool}</c>.
        /// </remarks>
        internal static string SanitizeToolNamePrefix(string serverName)
        {
            return string.Concat(serverName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        }

        private sealed class McpToolLeaseSet : IMcpToolLeaseSet
        {
            private readonly IReadOnlyList<IMcpToolLease> _leases;
            private int _disposed;

            public McpToolLeaseSet(IReadOnlyList<IMcpToolLease> leases, IReadOnlyList<McpServerReference> servers)
            {
                _leases = leases;
                Servers = servers;
                Tools = [.. leases.SelectMany(l => l.Tools)];
            }

            public static McpToolLeaseSet Empty { get; } = new([], []);

            public IReadOnlyList<AITool> Tools { get; }

            public IReadOnlyList<McpServerReference> Servers { get; }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                foreach (var lease in _leases)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
