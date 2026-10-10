using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class UserMcpCredentialServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IUserSecretProtector _protector = Substitute.For<IUserSecretProtector>();
    private readonly IMcpClientCache _mcpClientCache = Substitute.For<IMcpClientCache>();
    private readonly UserMcpCredentialService _service;

    public UserMcpCredentialServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _protector.Protect(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(callInfo => $"protected:{callInfo.Arg<string>()}");

        _service = new UserMcpCredentialService(
            NullLogger<UserMcpCredentialService>.Instance,
            _tokenService,
            _fixture.Context,
            new SaveMcpCredentialActionDtoValidator(),
            _protector,
            _mcpClientCache);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<McpServer> AddMcpServerAsync(
        string name,
        McpAuthTypes authType = McpAuthTypes.UserApiKey,
        bool granted = true,
        DateTimeOffset? dateDeactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            Url = $"https://mcp.example.com/{name}",
            AuthType = authType,
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

    private async Task<UserMcpCredential?> ReadCredentialAsync(Guid mcpServerId, Guid? userId = null)
    {
        using var ctx = _fixture.CreateContext();
        return await ctx.UserMcpCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.McpServerId == mcpServerId && c.UserId == (userId ?? KnownIds.SeedUserId),
                TestContext.Current.CancellationToken);
    }

    private static SaveMcpCredentialActionDto Request(string apiKey = "github_pat_abcdefgh")
    {
        return new SaveMcpCredentialActionDto { ApiKey = apiKey };
    }

    [Fact]
    public async Task SaveAsync_FirstKey_StoresItEncryptedAndReturnsOnlyTheHint()
    {
        var server = await AddMcpServerAsync("aaa-github");

        var status = await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(server.Id, status.McpServerId);
        Assert.True(status.HasApiKey);
        Assert.Equal("efgh", status.ApiKeyHint);
        Assert.NotNull(status.DateModified);

        var stored = await ReadCredentialAsync(server.Id);
        Assert.NotNull(stored);
        Assert.Equal("protected:github_pat_abcdefgh", stored.Ciphertext);
        Assert.Equal("efgh", stored.ApiKeyHint);
        Assert.Null(stored.DateRejected);
    }

    [Fact]
    public async Task SaveAsync_StoredCredential_NeverHoldsThePlaintext()
    {
        var server = await AddMcpServerAsync("aaa-plaintext");
        _protector.Protect(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>()).Returns("opaque");

        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        var stored = await ReadCredentialAsync(server.Id);
        Assert.DoesNotContain("github_pat_abcdefgh", stored!.Ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_SecondKey_ReplacesTheStoredOneRatherThanAddingARow()
    {
        var server = await AddMcpServerAsync("aaa-replace");
        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        var status = await _service.SaveAsync(
            server.Id, Request("github_pat_zzzzwxyz"), TestContext.Current.CancellationToken);

        Assert.Equal("wxyz", status.ApiKeyHint);

        using var ctx = _fixture.CreateContext();
        var credentials = await ctx.UserMcpCredentials
            .Where(c => c.McpServerId == server.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(credentials);
        Assert.Equal("protected:github_pat_zzzzwxyz", credentials[0].Ciphertext);
    }

    [Fact]
    public async Task SaveAsync_AfterARejection_ClearsIt()
    {
        var server = await AddMcpServerAsync("aaa-recover");
        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);
        await _service.MarkRejectedAsync(server.Id, KnownIds.SeedUserId, TestContext.Current.CancellationToken);

        var status = await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        Assert.True(status.HasApiKey);
        Assert.Null((await ReadCredentialAsync(server.Id))!.DateRejected);
    }

    [Fact]
    public async Task SaveAsync_Key_EvictsOnlyThatUsersCachedClient()
    {
        var server = await AddMcpServerAsync("aaa-evict");

        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        _mcpClientCache.Received(1).InvalidateServerForUser(server.Id, KnownIds.SeedUserId);
        _mcpClientCache.DidNotReceiveWithAnyArgs().InvalidateServer(default);
    }

    [Fact]
    public async Task SaveAsync_ServerTakingNoUserKey_IsRejectedWithoutStoringAnything()
    {
        var server = await AddMcpServerAsync("aaa-anonymous", McpAuthTypes.None);

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, e => e.PropertyName == nameof(SaveMcpCredentialActionDto.ApiKey));
        Assert.Null(await ReadCredentialAsync(server.Id));
    }

    [Fact]
    public async Task SaveAsync_OnBehalfOfServer_IsRejectedWithoutStoringAnything()
    {
        var server = await AddMcpServerAsync("aaa-obo", McpAuthTypes.EntraIdOnBehalfOf);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken));

        Assert.Null(await ReadCredentialAsync(server.Id));
    }

    [Fact]
    public async Task SaveAsync_ShortestAcceptedKey_DisclosesOnlyItsLastFourCharacters()
    {
        var server = await AddMcpServerAsync("aaa-hint");

        var status = await _service.SaveAsync(
            server.Id, Request("abcdefgh"), TestContext.Current.CancellationToken);

        Assert.Equal("efgh", status.ApiKeyHint);
    }

    [Fact]
    public async Task SaveAsync_ServerWithoutAGrant_IsForbidden()
    {
        var server = await AddMcpServerAsync("aaa-ungranted", granted: false);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken));

        Assert.Equal($"You do not have permission to use MCP server '{server.Name}'.", exception.Message);
        Assert.Null(await ReadCredentialAsync(server.Id));
    }

    [Fact]
    public async Task SaveAsync_UnknownServer_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.SaveAsync(Guid.NewGuid(), Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_DeactivatedServer_IsNotFound()
    {
        var server = await AddMcpServerAsync("aaa-retired", dateDeactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("github pat with spaces")]
    [InlineData("github_pat\nnewline")]
    public async Task SaveAsync_UnusableKey_FailsValidation(string apiKey)
    {
        var server = await AddMcpServerAsync("aaa-validation");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SaveAsync(server.Id, Request(apiKey), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_StoredKey_SoftDeletesItAndEvictsTheClient()
    {
        var server = await AddMcpServerAsync("aaa-remove");
        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);
        _mcpClientCache.ClearReceivedCalls();

        await _service.DeleteAsync(server.Id, TestContext.Current.CancellationToken);

        Assert.NotNull((await ReadCredentialAsync(server.Id))!.DateDeactivated);
        _mcpClientCache.Received(1).InvalidateServerForUser(server.Id, KnownIds.SeedUserId);
    }

    [Fact]
    public async Task DeleteAsync_NoStoredKey_IsNotFound()
    {
        var server = await AddMcpServerAsync("aaa-nothing-to-remove");

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeleteAsync(server.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_AfterRemoval_StoresAgainRatherThanCollidingOnTheUniqueIndex()
    {
        var server = await AddMcpServerAsync("aaa-resupply");
        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);
        await _service.DeleteAsync(server.Id, TestContext.Current.CancellationToken);

        var status = await _service.SaveAsync(
            server.Id, Request("github_pat_zzzzwxyz"), TestContext.Current.CancellationToken);

        Assert.True(status.HasApiKey);
        Assert.Equal("wxyz", status.ApiKeyHint);
    }

    [Fact]
    public async Task MarkRejectedAsync_StoredKey_StampsItSoItReadsAsAbsent()
    {
        var server = await AddMcpServerAsync("aaa-refused");
        await _service.SaveAsync(server.Id, Request(), TestContext.Current.CancellationToken);

        await _service.MarkRejectedAsync(server.Id, KnownIds.SeedUserId, TestContext.Current.CancellationToken);

        Assert.NotNull((await ReadCredentialAsync(server.Id))!.DateRejected);
    }

    [Fact]
    public async Task MarkRejectedAsync_NoStoredKey_DoesNothing()
    {
        var server = await AddMcpServerAsync("aaa-refused-nothing");

        await _service.MarkRejectedAsync(server.Id, KnownIds.SeedUserId, TestContext.Current.CancellationToken);

        Assert.Null(await ReadCredentialAsync(server.Id));
    }
}
