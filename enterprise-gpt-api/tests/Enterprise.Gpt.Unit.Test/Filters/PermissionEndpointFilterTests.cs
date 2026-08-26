using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Enterprise.Gpt.Api.Filters;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Filters;

public sealed class PermissionEndpointFilterTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private static UserPermissionCache CreateRealCache()
    {
        return new UserPermissionCache(
            NullLogger<UserPermissionCache>.Instance,
            Options.Create(new UserPermissionCacheOptions()),
            TimeProvider.System);
    }

    /// <summary>
    /// Builds a request context wired to <paramref name="cache"/>. <paramref name="ctx"/> is left
    /// unregistered by default so a test proves a cache hit never resolves a <c>DbContext</c>.
    /// </summary>
    private static EndpointFilterInvocationContext CreateContext(
        Guid oid, IUserPermissionCache cache, EnterpriseGptDbContext? ctx = null)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GetOid().Returns(oid);

        var services = new ServiceCollection();
        services.AddSingleton(tokenService);
        services.AddSingleton(cache);

        // The real reader over the substituted cache: the filter now asks the same question through
        // the same type the services do, so a test that stubbed the reader would stop covering the
        // loader the filter actually runs on a miss.
        services.AddScoped<IUserGrantReader, UserGrantReader>();

        if (ctx is not null)
        {
            services.AddSingleton(ctx);
        }

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        return EndpointFilterInvocationContext.Create(httpContext);
    }

    private static IUserPermissionCache CacheHolding(Guid oid, params Guid[] permissionIds)
    {
        var cache = Substitute.For<IUserPermissionCache>();
        cache.GetOrLoadAsync(oid, Arg.Any<Func<CancellationToken, Task<IReadOnlyCollection<Guid>>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(permissionIds.ToHashSet()));

        return cache;
    }

    private async Task AddUserWithGrantAsync(Guid userId, Guid permissionId, DateTimeOffset? dateDeactivated = null)
    {
        await using var ctx = _fixture.CreateContext();
        var date = DateTimeOffset.UtcNow;
        ctx.Add(new User
        {
            Id = userId,
            FirstName = "Test",
            LastName = "User",
            Email = $"{userId}@example.com",
            DateCreated = date,
            DateModified = date
        });
        ctx.Add(new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionId = permissionId,
            DateCreated = date,
            CreatedById = KnownIds.SeedUserId,
            DateModified = date,
            ModifiedById = KnownIds.SeedUserId,
            DateDeactivated = dateDeactivated
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_CallerHoldsThePermission_InvokesTheEndpoint()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);
        var invoked = false;

        await filter(CreateContext(oid, CacheHolding(oid, PermissionIds.Administrator)), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task Filter_CallerLacksThePermission_ShortCircuitsWithForbidden()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);
        var invoked = false;

        var result = await filter(CreateContext(oid, CacheHolding(oid)), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(invoked);

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        Assert.Equal(ProblemTypes.PermissionRequired.Type, problem.ProblemDetails.Type);
        Assert.Equal("Administrator permission is required.", problem.ProblemDetails.Detail);
        Assert.Equal<string[]>(["Administrator"], Assert.IsType<string[]>(problem.ProblemDetails.Extensions["permissions"]));
    }

    [Fact]
    public async Task Filter_CallerHoldsEveryRequiredPermission_InvokesTheEndpoint()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator, PermissionIds.UploadFile);
        var invoked = false;

        await filter(CreateContext(oid, CacheHolding(oid, PermissionIds.Administrator, PermissionIds.UploadFile)), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task Filter_CallerHoldsOnlyOneOfTwoRequiredPermissions_ReturnsForbidden()
    {
        // Require is AND, not OR: holding a subset is not enough.
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator, PermissionIds.UploadFile);
        var invoked = false;

        var result = await filter(CreateContext(oid, CacheHolding(oid, PermissionIds.Administrator)), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Filter_TwoRequiredPermissions_NamesBothInTheMessage()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator, PermissionIds.UploadFile);

        var result = await filter(CreateContext(oid, CacheHolding(oid)), _ => ValueTask.FromResult<object?>(null));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal("Administrator and Upload File permissions are required.", problem.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Filter_DuplicatePermissionIds_AreNamedOnce()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator, PermissionIds.Administrator);

        var result = await filter(CreateContext(oid, CacheHolding(oid)), _ => ValueTask.FromResult<object?>(null));

        var problem = Assert.IsType<ProblemHttpResult>(result);
        Assert.Equal("Administrator permission is required.", problem.ProblemDetails.Detail);
    }

    [Fact]
    public async Task Filter_CacheHit_NeverResolvesTheDbContext()
    {
        // No EnterpriseGptDbContext is registered, so a hit that touched EF would throw. This is
        // the whole point of the change: a warm request costs no database round trip.
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);
        var cache = CreateRealCache();
        cache.Set(oid, cache.CaptureInvalidationStamp(), [PermissionIds.Administrator]);
        var invoked = false;

        await filter(CreateContext(oid, cache), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task Filter_CacheMiss_LoadsTheGrantsFromTheDatabaseAndCachesThem()
    {
        var oid = Guid.NewGuid();
        await AddUserWithGrantAsync(oid, PermissionIds.Administrator);

        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);
        var cache = CreateRealCache();

        var firstInvoked = false;
        await filter(CreateContext(oid, cache, _fixture.Context), _ =>
        {
            firstInvoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        // Removing the row out of band leaves the cached entry as the only source of the grant, so
        // a second pass still succeeding proves the request never queried.
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.UserPermissions
                .Where(up => up.UserId == oid)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var secondInvoked = false;
        await filter(CreateContext(oid, cache, _fixture.Context), _ =>
        {
            secondInvoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(firstInvoked);
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task Filter_CacheMiss_ExcludesDeactivatedGrants()
    {
        var oid = Guid.NewGuid();
        await AddUserWithGrantAsync(oid, PermissionIds.Administrator, dateDeactivated: DateTimeOffset.UtcNow);

        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);
        var invoked = false;

        var result = await filter(CreateContext(oid, CreateRealCache(), _fixture.Context), _ =>
        {
            invoked = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Filter_UserWithNoGrants_ReturnsForbidden()
    {
        var oid = Guid.NewGuid();
        var filter = PermissionEndpointFilter.Require(PermissionIds.Administrator);

        var result = await filter(
            CreateContext(oid, CreateRealCache(), _fixture.Context), _ => ValueTask.FromResult<object?>(null));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ProblemHttpResult>(result).StatusCode);
    }

    [Fact]
    public void Require_NoPermissionIds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PermissionEndpointFilter.Require());
    }

    [Fact]
    public void Require_PermissionIdWithoutADisplayName_Throws()
    {
        // Fails at map time — i.e. at startup — rather than emitting a nameless 403 at runtime.
        Assert.Throws<ArgumentException>(() => PermissionEndpointFilter.Require(Guid.NewGuid()));
    }
}
