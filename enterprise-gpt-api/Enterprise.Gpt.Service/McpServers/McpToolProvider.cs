using System.Net;
using System.Net.Sockets;
using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Caching.Models;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.McpServers.Exceptions;
using Enterprise.Gpt.Service.McpServers.Models;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Enterprise.Gpt.Service.McpServers;

/// <summary>
/// Leased MCP tools for every server a chat request selected; dispose when the stream ends to return the
/// clients to the cache.
/// </summary>
public interface IMcpToolLeaseSet : IAsyncDisposable
{
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>
    /// The servers behind the tools, captured at lease time so a rename before the audit write is not
    /// recorded under the new name.
    /// </summary>
    IReadOnlyList<McpServerReference> Servers { get; }
}

public interface IMcpToolProvider
{
    /// <summary>
    /// Checks each selected server exists, is active and is permitted, then leases its tools through the
    /// client cache. Duplicate ids are ignored; the caller must dispose the result.
    /// </summary>
    /// <exception cref="NotFoundException">A selected server does not exist or is deactivated.</exception>
    /// <exception cref="ForbiddenException">The user lacks the permission for a selected server.</exception>
    /// <exception cref="McpServerUnavailableException">Connecting, listing tools or acquiring the on-behalf-of token failed, including consent or Conditional Access (a broken registration).</exception>
    /// <exception cref="McpCredentialRequiredException">A server takes a user API key and the caller has no usable one.</exception>
    /// <exception cref="McpCredentialRejectedException">A server refused the caller's stored API key.</exception>
    Task<IMcpToolLeaseSet> AcquireToolsAsync(IReadOnlyCollection<Guid> mcpServerIds, CancellationToken cancellationToken);
}

public partial class McpToolProvider(ILogger<McpToolProvider> logger,
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
    /// <summary>Named MCP HTTP client; registered with no timeout since sessions stream over SSE.</summary>
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

        // Deliberately not IUserPermissionCache: tool calls run code for the user on third-party servers, so
        // a revoked grant must apply on the next request. The query is negligible next to the MCP session.
        var servers = await _ctx.McpServers
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id) && !s.DateDeactivated.HasValue)
            .Select(s => new
            {
                Server = s,
                IsPermitted = s.Permissions.Any(p => p.UserPermissions.Any(up => up.UserId == oid)),
                // A refused key reads as absent, so the user is asked for a new one.
                Ciphertext = s.UserCredentials
                    .Where(c => c.UserId == oid && !c.DateRejected.HasValue)
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

    #region Private methods

    // Records refused keys so the next request asks for a new one. Not in the cache factory: the singleton
    // cache single-flights creation, so a factory may run for another request's scope.
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
            // Bookkeeping only: the caller sees the rejection anyway; losing this costs one more failed turn.
            catch (Exception ex)
            {
                LogApiKeyRejectionNotRecorded(_logger, ex, rejected.McpServerId, oid);
            }
        }
    }

    private async Task<IMcpToolLease> AcquireLeaseAsync(
        McpServer server, string? ciphertext, Guid oid, CancellationToken cancellationToken)
    {
        if (server.AuthType == McpAuthTypes.UserApiKey)
        {
            // Resolved in scoped code: the singleton cache must never touch scoped services.
            if (ciphertext is null
                || !_secretProtector.TryUnprotect(oid, server.Id, ciphertext, out var apiKey)
                || apiKey is null)
            {
                throw new McpCredentialRequiredException(server.Id, server.Name);
            }

            return await _mcpClientCache.GetOrCreateAsync(
                McpCacheKey.ForUser(server.Id, oid),
                // No expiry: the entry lives to the age cap; a replaced key relies on invalidation.
                token => CreateEntrySourceAsync(server, apiKey, tokenExpiresOn: null, token),
                cancellationToken);
        }

        if (server.AuthType == McpAuthTypes.EntraIdOnBehalfOf)
        {
            // Acquired here for the same reason. MSAL's cache makes repeats cheap, and an expired
            // entry gets a fresh token on recreation.
            var (accessToken, expiresOn) = await AcquireAccessTokenAsync(server);

            return await _mcpClientCache.GetOrCreateAsync(
                McpCacheKey.ForUser(server.Id, oid),
                token => CreateEntrySourceAsync(server, accessToken, expiresOn, token),
                cancellationToken);
        }

        // No token and the same tool list for everyone, so one entry is shared across users.
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
        // Deliberately one arm: servers are consented tenant-wide, so a UI-required MsalException is a broken
        // registration, not a user action; the challenge exception is not an MsalException, hence named. The
        // 502's Retry is accepted: it helps transient faults and merely repeats a mis-registration.
        catch (Exception ex) when (ex is MicrosoftIdentityWebChallengeUserException or MsalException)
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

            // Prefixed so names stay unique across servers (the SDK still calls the original), and
            // before WithTracking so the wrapper keeps the prefix. The catalog name, not ServerInfo's,
            // because the client and UsageReportTranslator strip the prefix with it. enableProgress
            // is explicit because the streamed sub-status lines depend on it.
            var prefix = SanitizeToolNamePrefix(server.Name);
            List<AITool> prefixedTools =
            [
                .. tools.Select(t => t.WithName($"{prefix}_{t.Name}").WithTracking(server.Name, enableProgress: true))
            ];

            // Names, never values: the names are the only in-app signal a restriction was applied, and the
            // values are administrator text with no place in a log.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var headerNames = additionalHeaders.Keys
                    .Where(k => !McpServerHeaderRules.IsReserved(k))
                    .Order()
                    .ToArray();
                var names = headerNames.Length == 0 ? "none" : string.Join(", ", headerNames);
                LogConnected(_logger, server.Id, prefixedTools.Count, names);
            }

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

    // Walks InnerExceptions as well as InnerException: the SDK reports its Streamable HTTP and SSE fallback
    // failures together, and an aggregate's InnerException would hide a 401 from the second transport.
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        return exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten).Prepend(aggregate),
            { InnerException: { } inner } => Flatten(inner).Prepend(exception),
            _ => [exception]
        };
    }

    // Failures that surface as 502. The transport's connection timeout arrives as a cancellation the caller
    // did not request, so it counts.
    private static bool IsConnectivityFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or McpException or TimeoutException or IOException or SocketException => true,
            // The SDK reports a Streamable HTTP failure and its SSE fallback together; unmatched, the
            // aggregate would reach the 500 default instead of a 502.
            AggregateException aggregate => aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(e => IsConnectivityFailure(e, cancellationToken)),
            // Net under BuildTransportHeaders: the SDK throws this for a refused header name, and as a
            // 500 its message, which quotes the header value, would be logged. ObjectDisposedException
            // derives from it but is our own lease bug, so it must not be reported as the server's 502.
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

    #endregion

    #region Public static methods

    /// <summary>
    /// Configured headers, then the bearer. <see cref="McpServerHeaderRules"/> is re-applied so a row that
    /// predates a rule or was written in SQL cannot send a header refused today; the bearer goes last into a
    /// case-insensitive map so a stored <c>authorization</c> is always overwritten.
    /// </summary>
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

    /// <summary>
    /// Maps a connect or list failure to a rejected user key or an unavailable server. Only user-key servers
    /// can yield the former: elsewhere a 401 is a registration fault the user cannot fix.
    /// </summary>
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
    /// The tool-name prefix, with everything outside <c>[A-Za-z0-9_-]</c> replaced by an underscore. Internal
    /// because usage reporting attributes a <c>{prefix}_{tool}</c> call back to its server with it.
    /// </summary>
    internal static string SanitizeToolNamePrefix(string serverName)
    {
        return string.Concat(serverName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record that MCP server {McpServerId} rejected the API key of user {UserId}")]
    private static partial void LogApiKeyRejectionNotRecorded(
        ILogger logger, Exception exception, Guid mcpServerId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to MCP server {McpServerId} and listed {ToolCount} tools with headers {HeaderNames}")]
    private static partial void LogConnected(ILogger logger, Guid mcpServerId, int toolCount, string headerNames);

    #endregion
}
