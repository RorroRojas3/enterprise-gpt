using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class McpToolProviderTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ITokenAcquisition _tokenAcquisition = Substitute.For<ITokenAcquisition>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IMcpClientCache _mcpClientCache = Substitute.For<IMcpClientCache>();
    private readonly McpToolProvider _provider;

    public McpToolProviderTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _provider = new McpToolProvider(
            NullLogger<McpToolProvider>.Instance,
            _tokenService,
            _tokenAcquisition,
            _httpClientFactory,
            NullLoggerFactory.Instance,
            Options.Create(new McpClientCacheOptions()),
            _mcpClientCache,
            _fixture.Context);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<McpServer> AddMcpServerAsync(
        string name, McpAuthTypes authType = McpAuthTypes.None, string? scope = null,
        DateTimeOffset? dateDeactivated = null, bool granted = true)
    {
        var date = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = authType,
            Scope = scope,
            DateDeactivated = dateDeactivated,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };
        var permission = new Permission
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"Access to the {name} MCP server.",
            McpServerId = server.Id,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        };

        using var ctx = _fixture.CreateContext();
        ctx.McpServers.Add(server);
        ctx.Permissions.Add(permission);
        if (granted)
        {
            ctx.UserPermissions.Add(new UserPermission
            {
                Id = Guid.NewGuid(),
                UserId = KnownIds.SeedUserId,
                PermissionId = permission.Id,
                DateCreated = date,
                DateModified = date,
                CreatedById = KnownIds.SeedUserId,
                ModifiedById = KnownIds.SeedUserId
            });
        }

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return server;
    }

    private static IMcpToolLease CreateLease(params AITool[] tools)
    {
        var lease = Substitute.For<IMcpToolLease>();
        lease.Tools.Returns(tools);
        return lease;
    }

    private void SetUpCache(IMcpToolLease lease)
    {
        _mcpClientCache.GetOrCreateAsync(
                Arg.Any<McpCacheKey>(),
                Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
                Arg.Any<CancellationToken>())
            .Returns(lease);
    }

    private static AuthenticationResult CreateAuthenticationResult(string scope)
    {
        // MSAL's public test constructor; product code never news this up.
        return new AuthenticationResult(
            accessToken: "test-access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: KnownIds.SeedUserId.ToString(),
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: "test-tenant",
            account: null,
            idToken: null,
            scopes: [scope],
            correlationId: Guid.NewGuid());
    }

    [Fact]
    public async Task AcquireToolsAsync_EmptyIdList_ReturnsEmptyToolsWithoutTouchingCache()
    {
        var result = await _provider.AcquireToolsAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(result.Tools);
        Assert.Empty(_mcpClientCache.ReceivedCalls());
    }

    [Fact]
    public async Task AcquireToolsAsync_UnknownServerId_ThrowsNotFoundExceptionNamingId()
    {
        var missingId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _provider.AcquireToolsAsync([missingId], TestContext.Current.CancellationToken));

        Assert.Equal($"MCP server {missingId} not found.", exception.Message);
    }

    [Fact]
    public async Task AcquireToolsAsync_DeactivatedServer_ThrowsNotFoundExceptionNamingId()
    {
        var server = await AddMcpServerAsync("aaa-deactivated", dateDeactivated: DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<NotFoundException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        Assert.Equal($"MCP server {server.Id} not found.", exception.Message);
    }

    [Fact]
    public async Task AcquireToolsAsync_ActiveServerWithoutGrant_ThrowsForbiddenExceptionNamingServer()
    {
        var server = await AddMcpServerAsync("aaa-ungranted", granted: false);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        Assert.Equal($"You do not have permission to use MCP server '{server.Name}'.", exception.Message);
    }

    [Fact]
    public async Task AcquireToolsAsync_AuthTypeNone_UsesSharedKeyAndSkipsTokenAcquisition()
    {
        var server = await AddMcpServerAsync("aaa-anonymous");
        var tool = new FakeTool();
        SetUpCache(CreateLease(tool));

        var result = await _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken);

        Assert.Equal([tool], result.Tools);
        await _mcpClientCache.Received(1).GetOrCreateAsync(
            McpCacheKey.Shared(server.Id),
            Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
            Arg.Any<CancellationToken>());
        Assert.Empty(_tokenAcquisition.ReceivedCalls());
    }

    [Fact]
    public async Task AcquireToolsAsync_DuplicateServerIds_ResolvesServerOnce()
    {
        var server = await AddMcpServerAsync("aaa-duplicated");
        SetUpCache(CreateLease(new FakeTool()));

        await _provider.AcquireToolsAsync([server.Id, server.Id], TestContext.Current.CancellationToken);

        await _mcpClientCache.Received(1).GetOrCreateAsync(
            Arg.Any<McpCacheKey>(),
            Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireToolsAsync_OnBehalfOfServer_AcquiresTokenForScopeAndUsesPerUserKey()
    {
        const string scope = "api://client-id/access_as_user";
        var server = await AddMcpServerAsync("aaa-obo", McpAuthTypes.EntraIdOnBehalfOf, scope);
        _tokenAcquisition.GetAuthenticationResultForUserAsync(Arg.Any<IEnumerable<string>>())
            .Returns(CreateAuthenticationResult(scope));
        SetUpCache(CreateLease(new FakeTool()));

        await _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken);

        await _tokenAcquisition.Received(1).GetAuthenticationResultForUserAsync(
            Arg.Is<IEnumerable<string>>(scopes => scopes!.Single() == scope));
        await _mcpClientCache.Received(1).GetOrCreateAsync(
            McpCacheKey.ForUser(server.Id, KnownIds.SeedUserId),
            Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
            Arg.Any<CancellationToken>());
    }

    public static TheoryData<string, Exception> TokenAcquisitionFailures() => new()
    {
        // Consent or Conditional Access, reported by MSAL itself. Reaches the same arm as any
        // other MsalException by derivation, which is the whole point of the fold.
        { "ui-required", new MsalUiRequiredException("interaction_required", "User consent is required.") },
        // The same requirement, wrapped by Microsoft.Identity.Web. This one derives from
        // Exception rather than MsalException, so it has to be named separately or it escapes.
        {
            "challenge",
            new MicrosoftIdentityWebChallengeUserException(
                new MsalUiRequiredException("interaction_required", "User consent is required."),
                ["api://client-id/access_as_user"])
        },
        // An ordinary service failure, to pin that the fold widened the arm rather than narrowing it.
        { "service", new MsalServiceException("service_unavailable", "The service is unavailable.") },
    };

    [Theory]
    [MemberData(nameof(TokenAcquisitionFailures))]
    public async Task AcquireToolsAsync_TokenAcquisitionFails_ThrowsMcpServerUnavailableException(
        string label, Exception failure)
    {
        var server = await AddMcpServerAsync(
            $"aaa-token-{label}", McpAuthTypes.EntraIdOnBehalfOf, "api://client-id/access_as_user");
        _tokenAcquisition.GetAuthenticationResultForUserAsync(Arg.Any<IEnumerable<string>>())
            .ThrowsAsync(failure);

        var exception = await Assert.ThrowsAsync<McpServerUnavailableException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        Assert.Equal(server.Name, exception.ServerName);
        Assert.Same(failure, exception.InnerException);
        Assert.Empty(_mcpClientCache.ReceivedCalls());
    }

    [Fact]
    public async Task AcquireToolsAsync_OneServerUnavailable_DisposesSuccessfulSiblingLease()
    {
        var goodServer = await AddMcpServerAsync("aaa-good");
        var failingServer = await AddMcpServerAsync("bbb-failing");
        var goodLease = CreateLease(new FakeTool());
        _mcpClientCache.GetOrCreateAsync(
                Arg.Any<McpCacheKey>(),
                Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<McpCacheKey>();
                if (key.McpServerId == failingServer.Id)
                {
                    throw new McpServerUnavailableException(failingServer.Name, new TimeoutException("connect timed out"));
                }

                return goodLease;
            });

        await Assert.ThrowsAsync<McpServerUnavailableException>(
            () => _provider.AcquireToolsAsync([goodServer.Id, failingServer.Id], TestContext.Current.CancellationToken));

        await goodLease.Received(1).DisposeAsync();
    }

    /// <summary>
    /// Pins the exact substitution the tool-name prefix is built from.
    /// </summary>
    /// <remarks>
    /// Two consumers undo this prefix and neither can see this method: the client, which strips it
    /// to label an activity card with the tool that ran, reimplements it as a regular expression;
    /// and <c>UsageReportTranslator</c>, which rebuilds the prefix to attribute a call back to a
    /// catalog server. The cases below are the ones where a looser rule — a <c>\w</c> class, or
    /// anything Unicode-aware — silently disagrees, which is the drift worth catching here rather
    /// than in a mislabelled card nobody reports.
    /// </remarks>
    [Theory]
    [InlineData("Weather", "Weather")]
    // Kept, not replaced: a hyphenated server is the case a `\w` class gets wrong.
    [InlineData("My-Server", "My-Server")]
    [InlineData("weather_api", "weather_api")]
    [InlineData("Andes Test MCP", "Andes_Test_MCP")]
    [InlineData("Jira Cloud (EU)", "Jira_Cloud__EU_")]
    // One underscore per UTF-16 code unit, because the substitution iterates chars.
    [InlineData("Météo", "M_t_o")]
    public void SanitizeToolNamePrefix_ServerName_ReplacesEverythingOutsideTheAsciiToolNameClass(
        string serverName, string expected)
    {
        Assert.Equal(expected, McpToolProvider.SanitizeToolNamePrefix(serverName));
    }

    /// <summary>
    /// The single most important assertion in the headers feature: a stored <c>Authorization</c>
    /// cannot displace the on-behalf-of bearer, in any casing.
    /// </summary>
    /// <remarks>
    /// The validators refuse the name on write, so reaching this needs a row that predates those
    /// rules or one written straight into SQL. Both are exactly the cases this guard exists for,
    /// which is why it is asserted here rather than trusted from the validator tests.
    /// </remarks>
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    public void BuildTransportHeaders_StoredAuthorization_CannotDisplaceTheBearer(string name)
    {
        var headers = McpToolProvider.BuildTransportHeaders(
            new Dictionary<string, string> { [name] = "Bearer attacker-token" },
            accessToken: "real-token");

        Assert.Equal("Bearer real-token", Assert.Contains("Authorization", headers));
        Assert.Single(headers);
    }

    [Fact]
    public void BuildTransportHeaders_ConfiguredHeaders_AreSentBesideTheBearer()
    {
        var headers = McpToolProvider.BuildTransportHeaders(
            new Dictionary<string, string>
            {
                ["X-MCP-Readonly"] = "true",
                ["X-MCP-Toolsets"] = "repos,wit,wiki"
            },
            accessToken: "real-token");

        Assert.Equal("true", Assert.Contains("X-MCP-Readonly", headers));
        Assert.Equal("repos,wit,wiki", Assert.Contains("X-MCP-Toolsets", headers));
        Assert.Equal("Bearer real-token", Assert.Contains("Authorization", headers));
    }

    /// <summary>
    /// The MCP protocol headers, which the "bearer written last" lock cannot defend.
    /// </summary>
    /// <remarks>
    /// <c>CopyAdditionalHeaders</c> sets these on the request before copying ours, and
    /// <c>TryAddWithoutValidation</c> appends rather than replaces — so the collision would happen
    /// inside <c>HttpRequestHeaders</c>, past anything this dictionary can order. Dropping them
    /// here is the only thing that stops a stored session id riding along on every user's request.
    /// </remarks>
    [Theory]
    [InlineData("Mcp-Session-Id")]
    [InlineData("mcp-session-id")]
    [InlineData("MCP-Protocol-Version")]
    [InlineData("Last-Event-ID")]
    public void BuildTransportHeaders_McpProtocolHeader_IsDropped(string name)
    {
        var headers = McpToolProvider.BuildTransportHeaders(
            new Dictionary<string, string> { [name] = "attacker-value" },
            accessToken: "real-token");

        Assert.DoesNotContain(name, headers);
        Assert.Single(headers);
        Assert.Equal("Bearer real-token", Assert.Contains("Authorization", headers));
    }

    [Theory]
    // Reserved by a name other than Authorization.
    [InlineData("Cookie", "session=1")]
    // A content header: `HttpRequestHeaders.TryAddWithoutValidation` refuses these outright, and
    // the SDK turns the refusal into a throw that would otherwise surface as an opaque 500.
    [InlineData("Content-Encoding", "gzip")]
    [InlineData("Expires", "0")]
    // Malformed name.
    [InlineData("X MCP Readonly", "true")]
    // A value carrying the CRLF that would split one header into two.
    [InlineData("X-MCP-Toolsets", "repos\r\nX-MCP-Readonly: false")]
    public void BuildTransportHeaders_HeaderTheRulesRefuse_IsDropped(string name, string value)
    {
        var headers = McpToolProvider.BuildTransportHeaders(
            new Dictionary<string, string> { [name] = value },
            accessToken: null);

        Assert.Empty(headers);
    }

    [Fact]
    public void BuildTransportHeaders_NothingConfiguredAndNoToken_IsEmpty()
    {
        // Empty rather than a dictionary of nothing: `CreateEntrySourceAsync` leaves
        // `AdditionalHeaders` unset on this arm, which is what it did before headers existed.
        Assert.Empty(McpToolProvider.BuildTransportHeaders(null, accessToken: null));
    }

    [Fact]
    public void BuildTransportHeaders_NoToken_StillCarriesConfiguredHeaders()
    {
        var headers = McpToolProvider.BuildTransportHeaders(
            new Dictionary<string, string> { ["X-MCP-Readonly"] = "true" },
            accessToken: null);

        Assert.Equal("true", Assert.Contains("X-MCP-Readonly", headers));
        Assert.DoesNotContain("Authorization", headers);
    }

    private sealed class FakeTool : AITool
    {
    }
}
