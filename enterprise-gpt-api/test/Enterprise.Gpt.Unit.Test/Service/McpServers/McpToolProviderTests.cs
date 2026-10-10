using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;
using System.Net;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class McpToolProviderTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ITokenAcquisition _tokenAcquisition = Substitute.For<ITokenAcquisition>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IMcpClientCache _mcpClientCache = Substitute.For<IMcpClientCache>();
    private readonly IUserSecretProtector _secretProtector = Substitute.For<IUserSecretProtector>();
    private readonly IUserMcpCredentialService _credentialService = Substitute.For<IUserMcpCredentialService>();
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
            _secretProtector,
            _credentialService,
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

    private async Task AddCredentialAsync(Guid mcpServerId, Guid userId, bool rejected = false)
    {
        var date = DateTimeOffset.UtcNow;

        using var ctx = _fixture.CreateContext();

        // The owning user has to exist for the foreign key; only the seed user is there already.
        if (userId != KnownIds.SeedUserId)
        {
            ctx.Users.Add(new User
            {
                Id = userId,
                FirstName = "Other",
                LastName = "User",
                Email = $"{userId:N}@example.com",
                DateCreated = date,
                DateModified = date
            });
        }

        ctx.UserMcpCredentials.Add(new UserMcpCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            McpServerId = mcpServerId,
            Ciphertext = "protected",
            ApiKeyHint = "cdef",
            DateRejected = rejected ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = KnownIds.SeedUserId,
            ModifiedById = KnownIds.SeedUserId
        });

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task AcquireToolsAsync_UserApiKeyServerWithStoredKey_DecryptsItAndUsesPerUserKey()
    {
        var server = await AddMcpServerAsync("aaa-pat", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, KnownIds.SeedUserId);
        _secretProtector
            .TryUnprotect(KnownIds.SeedUserId, server.Id, "protected", out Arg.Any<string?>())
            .Returns(callInfo =>
            {
                callInfo[3] = "ghp_secret";
                return true;
            });
        SetUpCache(CreateLease(new FakeTool()));

        await _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken);

        await _mcpClientCache.Received(1).GetOrCreateAsync(
            McpCacheKey.ForUser(server.Id, KnownIds.SeedUserId),
            Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
            Arg.Any<CancellationToken>());
        await _tokenAcquisition.DidNotReceiveWithAnyArgs().GetAuthenticationResultForUserAsync(default!);
    }

    [Fact]
    public async Task AcquireToolsAsync_UserApiKeyServerWithNoStoredKey_ThrowsCredentialRequired()
    {
        var server = await AddMcpServerAsync("aaa-pat-missing", McpAuthTypes.UserApiKey);

        var exception = await Assert.ThrowsAsync<McpCredentialRequiredException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        Assert.Equal(server.Id, exception.McpServerId);
        Assert.Equal(server.Name, exception.ServerName);
        Assert.Empty(_mcpClientCache.ReceivedCalls());
    }

    [Fact]
    public async Task AcquireToolsAsync_StoredKeyThatWillNotDecrypt_ThrowsCredentialRequired()
    {
        var server = await AddMcpServerAsync("aaa-pat-undecryptable", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, KnownIds.SeedUserId);
        _secretProtector
            .TryUnprotect(KnownIds.SeedUserId, server.Id, "protected", out Arg.Any<string?>())
            .Returns(false);

        await Assert.ThrowsAsync<McpCredentialRequiredException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireToolsAsync_AlreadyRejectedKey_ReadsAsAbsent()
    {
        var server = await AddMcpServerAsync("aaa-pat-rejected", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, KnownIds.SeedUserId, rejected: true);

        await Assert.ThrowsAsync<McpCredentialRequiredException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        // Never opened: the row is excluded by the query, not discarded after decryption.
        _secretProtector.DidNotReceiveWithAnyArgs().TryUnprotect(default, default, default!, out Arg.Any<string?>());
    }

    [Fact]
    public async Task AcquireToolsAsync_AnotherUsersStoredKey_IsNotUsed()
    {
        var server = await AddMcpServerAsync("aaa-pat-other-user", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, Guid.NewGuid());

        await Assert.ThrowsAsync<McpCredentialRequiredException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireToolsAsync_ServerRejectsTheKey_RecordsTheRejection()
    {
        var server = await AddMcpServerAsync("aaa-pat-refused", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, KnownIds.SeedUserId);
        _secretProtector
            .TryUnprotect(KnownIds.SeedUserId, server.Id, "protected", out Arg.Any<string?>())
            .Returns(callInfo =>
            {
                callInfo[3] = "ghp_revoked";
                return true;
            });
        _mcpClientCache.GetOrCreateAsync(
                Arg.Any<McpCacheKey>(),
                Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new McpCredentialRejectedException(
                server.Id, server.Name, new HttpRequestException("unauthorized")));

        await Assert.ThrowsAsync<McpCredentialRejectedException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));

        await _credentialService.Received(1).MarkRejectedAsync(
            server.Id, KnownIds.SeedUserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireToolsAsync_RecordingARejectionFails_StillReportsTheRejection()
    {
        var server = await AddMcpServerAsync("aaa-pat-bookkeeping", McpAuthTypes.UserApiKey);
        await AddCredentialAsync(server.Id, KnownIds.SeedUserId);
        _secretProtector
            .TryUnprotect(KnownIds.SeedUserId, server.Id, "protected", out Arg.Any<string?>())
            .Returns(callInfo =>
            {
                callInfo[3] = "ghp_revoked";
                return true;
            });
        _mcpClientCache.GetOrCreateAsync(
                Arg.Any<McpCacheKey>(),
                Arg.Any<Func<CancellationToken, Task<McpCacheEntrySource>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new McpCredentialRejectedException(
                server.Id, server.Name, new HttpRequestException("unauthorized")));
        _credentialService.MarkRejectedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("write failed"));

        await Assert.ThrowsAsync<McpCredentialRejectedException>(
            () => _provider.AcquireToolsAsync([server.Id], TestContext.Current.CancellationToken));
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

    private static McpServer ServerFor(McpAuthTypes authType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "GitHub",
        Description = "GitHub",
        Url = "https://api.githubcopilot.com/mcp/",
        AuthType = authType
    };

    /// <summary>
    /// Pins the branch that decides who is told to fix a failed connection. Getting it wrong
    /// either blames a third-party outage on the user's key, or hides a revoked key behind a
    /// Retry button that can never succeed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Classify_UserApiKeyServerRefusedByTheHost_IsACredentialRejection(HttpStatusCode status)
    {
        var server = ServerFor(McpAuthTypes.UserApiKey);
        // Wrapped as the SDK reports it: the status lives on an inner exception, not the one caught.
        var failure = new McpException("listing tools failed", new HttpRequestException("refused", null, status));

        var classified = McpToolProvider.Classify(server, failure);

        var rejected = Assert.IsType<McpCredentialRejectedException>(classified);
        Assert.Equal(server.Id, rejected.McpServerId);
        Assert.Same(failure, rejected.InnerException);
    }

    [Fact]
    public void Classify_RefusalOnTheSdkFallbackBranch_IsStillACredentialRejection()
    {
        // Streamable HTTP and its SSE fallback both failed, so the SDK surfaces the two together.
        // `InnerException` on an aggregate returns only the first, which is what hid this.
        var failure = new AggregateException(
            new McpException("streamable http failed", new IOException("connection reset")),
            new McpException("sse failed", new HttpRequestException("refused", null, HttpStatusCode.Unauthorized)));

        var classified = McpToolProvider.Classify(ServerFor(McpAuthTypes.UserApiKey), failure);

        Assert.IsType<McpCredentialRejectedException>(classified);
    }

    [Theory]
    [InlineData(McpAuthTypes.None)]
    [InlineData(McpAuthTypes.EntraIdOnBehalfOf)]
    public void Classify_RefusalOnAServerHoldingNoUserKey_IsUnavailable(McpAuthTypes authType)
    {
        // Nothing the user supplied, so nothing they can replace: this is a registration fault.
        var failure = new McpException("failed", new HttpRequestException("refused", null, HttpStatusCode.Unauthorized));

        Assert.IsType<McpServerUnavailableException>(McpToolProvider.Classify(ServerFor(authType), failure));
    }

    [Fact]
    public void Classify_FailureThatIsNotARefusal_IsUnavailable()
    {
        var failure = new McpException("failed", new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));

        Assert.IsType<McpServerUnavailableException>(
            McpToolProvider.Classify(ServerFor(McpAuthTypes.UserApiKey), failure));
    }

    private sealed class FakeTool : AITool
    {
    }
}
