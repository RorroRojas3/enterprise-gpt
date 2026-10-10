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
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Settings;
using System.Net;
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
        /// <exception cref="McpServerUnavailableException">Connecting to a server, listing its tools, or acquiring an on-behalf-of token for it failed — including a consent or Conditional Access requirement, which for a tenant-consented server means a broken registration rather than a user-actionable state.</exception>
        /// <exception cref="McpCredentialRequiredException">A selected server takes a user-supplied API key and the caller has no usable one stored.</exception>
        /// <exception cref="McpCredentialRejectedException">A selected server refused the API key the caller stored.</exception>
        Task<IMcpToolLeaseSet> AcquireToolsAsync(IReadOnlyCollection<Guid> mcpServerIds, CancellationToken cancellationToken);
    }

    public class McpToolProvider(ILogger<McpToolProvider> logger,
        ITokenService tokenService,
        ITokenAcquisition tokenAcquisition,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IOptions<McpClientCacheOptions> options,
        IMcpClientCache mcpClientCache,
        IUserSecretProtector secretProtector,
        IUserMcpCredentialService credentialService,
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
        private readonly IUserSecretProtector _secretProtector = secretProtector;
        private readonly IUserMcpCredentialService _credentialService = credentialService;
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
                        && p.UserPermissions.Any(up => up.UserId == oid && !up.DateDeactivated.HasValue)),
                    // Joined onto the query that had to run anyway rather than read per server as
                    // each lease is acquired. A rejected row is excluded, so a key the server has
                    // already refused reads as absent and the user is asked for a new one.
                    Ciphertext = s.UserCredentials
                        .Where(c => c.UserId == oid && !c.DateDeactivated.HasValue && !c.DateRejected.HasValue)
                        .Select(c => c.Ciphertext)
                        .FirstOrDefault()
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

            var leaseTasks = servers
                .Select(x => AcquireLeaseAsync(x.Server, x.Ciphertext, oid, cancellationToken))
                .ToArray();
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

                await MarkRejectedCredentialsAsync(leaseTasks, oid, cancellationToken);

                throw;
            }
        }

        /// <summary>
        /// Records every credential a server refused during this acquisition, so the next request
        /// asks the user for a new one instead of failing the same way.
        /// </summary>
        /// <remarks>
        /// Here rather than inside the cache factory: the singleton cache single-flights creation,
        /// so a factory can run on behalf of a request whose scope is not the one writing this.
        /// </remarks>
        private async Task MarkRejectedCredentialsAsync(
            IReadOnlyList<Task<IMcpToolLease>> leaseTasks, Guid oid, CancellationToken cancellationToken)
        {
            foreach (var task in leaseTasks)
            {
                if (task.Exception?.InnerExceptions.OfType<McpCredentialRejectedException>().FirstOrDefault()
                    is not { } rejected)
                {
                    continue;
                }

                try
                {
                    await _credentialService.MarkRejectedAsync(rejected.McpServerId, oid, cancellationToken);
                }
                // Bookkeeping, not the outcome: the caller sees the rejection either way, and
                // losing this write costs them one more failed turn, nothing more.
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not record that MCP server {McpServerId} rejected the API key of user {UserId}",
                        rejected.McpServerId, oid);
                }
            }
        }

        private async Task<IMcpToolLease> AcquireLeaseAsync(
            McpServer server, string? ciphertext, Guid oid, CancellationToken cancellationToken)
        {
            if (server.AuthType == McpAuthTypes.UserApiKey)
            {
                // Decrypted here for the reason the on-behalf-of token is acquired here: the cache
                // is a singleton and must never touch scoped services, so the credential is
                // resolved in scoped code and captured by the factory closure.
                if (ciphertext is null
                    || !_secretProtector.TryUnprotect(oid, server.Id, ciphertext, out var apiKey)
                    || apiKey is null)
                {
                    throw new McpCredentialRequiredException(server.Id, server.Name);
                }

                return await _mcpClientCache.GetOrCreateAsync(
                    McpCacheKey.ForUser(server.Id, oid),
                    // No expiry: a user-issued token carries none, so the entry lives to the
                    // cache's age cap and a replaced key depends on explicit invalidation.
                    token => CreateEntrySourceAsync(server, apiKey, tokenExpiresOn: null, token),
                    cancellationToken);
            }

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
            // One arm for every token-acquisition failure, deliberately. `MsalUiRequiredException`
            // derives from `MsalException`, so a consent or Conditional Access requirement lands
            // here too: these servers are consented tenant-wide by an administrator, which makes a
            // UI-required result a broken registration rather than something the signed-in user can
            // act on. `MicrosoftIdentityWebChallengeUserException` derives from `Exception`, not
            // from `MsalException`, so it has to be named as well.
            //
            // The client offers Retry on this arm and did not on the consent problem this replaced.
            // That is accepted rather than overlooked: 502 also covers genuinely transient service
            // failures, where a retry does help, and a second attempt against a mis-registered
            // server merely fails the same way instead of misleading the reader about who can fix it.
            catch (Exception ex) when (ex is MicrosoftIdentityWebChallengeUserException or MsalException)
            {
                throw new McpServerUnavailableException(server.Name, ex);
            }
        }

        /// <summary>
        /// Builds the request headers for one server: its configured headers, then the
        /// on-behalf-of bearer.
        /// </summary>
        /// <param name="configured">The headers stored on the server row, if any.</param>
        /// <param name="accessToken">The on-behalf-of token, or <see langword="null"/> for a server with no authentication.</param>
        /// <returns>The headers to send, empty when there are none.</returns>
        /// <remarks>
        /// <see cref="McpServerHeaderRules"/> is applied again here rather than trusted from write
        /// time. A row that predates a rule change, or one written straight into SQL by a DBA,
        /// must not be able to set a header this application would refuse today — so a name that
        /// is reserved or malformed, or a value that is not printable ASCII, is dropped silently
        /// rather than sent.
        ///
        /// Ordinal-ignore-case, and the bearer written last: a stored `authorization` header
        /// collapses onto the same slot and is overwritten whatever casing it was persisted under.
        /// That is the lock that holds even if this filter is one day loosened.
        ///
        /// <c>internal</c> rather than <c>private</c> for the reason
        /// <see cref="SanitizeToolNamePrefix"/> is: it is the only way to pin this behaviour in a
        /// unit test.
        /// </remarks>
        internal static Dictionary<string, string> BuildTransportHeaders(
            IReadOnlyDictionary<string, string>? configured, string? accessToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (configured is not null)
            {
                foreach (var (name, value) in configured)
                {
                    if (McpServerHeaderRules.IsWellFormedName(name)
                        && !McpServerHeaderRules.IsReserved(name)
                        && McpServerHeaderRules.IsWellFormedValue(value))
                    {
                        headers[name] = value;
                    }
                }
            }

            if (accessToken is not null)
            {
                headers["Authorization"] = $"Bearer {accessToken}";
            }

            return headers;
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

            var additionalHeaders = BuildTransportHeaders(server.Headers, accessToken);
            if (additionalHeaders.Count > 0)
            {
                transportOptions.AdditionalHeaders = additionalHeaders;
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
                throw Classify(server, ex);
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

                // Header names, never values: a name is what tells an operator a restriction was
                // actually applied, and is the only in-app signal that it was, since nothing
                // surfaces a server's tool surface. Values are configuration but still the
                // administrator's text, and have no place in a log.
                // Joined here rather than passed as a sequence: this is a plain `ILogger`, whose
                // default formatter would render an enumerable as its type name.
                var headerNames = additionalHeaders.Keys
                    .Where(k => !McpServerHeaderRules.IsReserved(k))
                    .Order()
                    .ToArray();

                _logger.LogInformation(
                    "Connected to MCP server {McpServerId} and listed {ToolCount} tools with headers {HeaderNames}",
                    server.Id,
                    prefixedTools.Count,
                    headerNames.Length == 0 ? "none" : string.Join(", ", headerNames));

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
                throw Classify(server, ex);
            }
            catch
            {
                // Cancellation or an unexpected failure: the connection must not leak either way.
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Turns a connect or list failure into the exception that names who can fix it: a refused
        /// user-supplied key, or an unavailable server.
        /// </summary>
        /// <remarks>
        /// Only servers holding a user's own credential can produce the first: a 401 from an
        /// on-behalf-of or anonymous server is a registration fault, and telling that user to
        /// replace a key they never supplied would send them nowhere.
        ///
        /// <c>internal</c> for the reason <see cref="BuildTransportHeaders"/> is: this is the
        /// branch that decides who is told to fix a failure, and a unit test is the only way to
        /// pin it.
        /// </remarks>
        internal static Exception Classify(McpServer server, Exception exception)
        {
            if (server.AuthType != McpAuthTypes.UserApiKey)
            {
                return new McpServerUnavailableException(server.Name, exception);
            }

            return Flatten(exception).Any(e => e is HttpRequestException
            {
                StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            })
                ? new McpCredentialRejectedException(server.Id, server.Name, exception)
                : new McpServerUnavailableException(server.Name, exception);
        }

        /// <summary>
        /// Every exception reachable from one, across both edges of the graph.
        /// </summary>
        /// <remarks>
        /// <see cref="AggregateException.InnerExceptions"/> as well as
        /// <see cref="Exception.InnerException"/>, because the SDK falls back from Streamable HTTP
        /// to SSE and reports both failures together — and <c>InnerException</c> on an aggregate
        /// returns only the first, so a 401 raised on the other transport would be invisible.
        /// </remarks>
        private static IEnumerable<Exception> Flatten(Exception exception)
        {
            return exception switch
            {
                AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten).Prepend(aggregate),
                { InnerException: { } inner } => Flatten(inner).Prepend(exception),
                _ => [exception]
            };
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
                // The SDK reports a Streamable-HTTP failure and its SSE fallback together. Without
                // this arm the aggregate matches nothing and reaches the handler's 500 default —
                // a worse answer than the 502 either branch alone would have produced.
                AggregateException aggregate => aggregate.InnerExceptions.Count > 0
                    && aggregate.InnerExceptions.All(e => IsConnectivityFailure(e, cancellationToken)),
                // The SDK's `CopyAdditionalHeaders` throws this when `HttpRequestHeaders` refuses a
                // header name. `_reservedNames` now covers every name it refuses and
                // `BuildTransportHeaders` drops them, so this is unreachable today — it is the net
                // under a future loosening of that set, and the reason it is worth keeping is what
                // happens without it: `GlobalExceptionHandler` maps a bare
                // `InvalidOperationException` to 400 with `exception.Message`, and the SDK's
                // message quotes the offending header's **value**. That would put a configured
                // value on the wire, which is the one thing this feature promises it never does.
                //
                // `ObjectDisposedException` is excluded because it derives from this one and means
                // something else entirely here: a disposed client or double-disposed transport is
                // a lease-lifecycle bug of ours, and reporting it as 502 would hide a deterministic
                // fault behind a Retry button and blame the third-party server for it.
                InvalidOperationException and not ObjectDisposedException => true,
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

        private sealed class McpToolLeaseSet(IReadOnlyList<IMcpToolLease> leases, IReadOnlyList<McpServerReference> servers) : IMcpToolLeaseSet
        {
            private readonly IReadOnlyList<IMcpToolLease> _leases = leases;
            private int _disposed;

            public static McpToolLeaseSet Empty { get; } = new([], []);

            public IReadOnlyList<AITool> Tools { get; } = [.. leases.SelectMany(l => l.Tools)];

            public IReadOnlyList<McpServerReference> Servers { get; } = servers;

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
