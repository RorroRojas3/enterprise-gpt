using Enterprise.Gpt.Repository.Sql.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Service.Caching;

/// <summary>
/// Reads a user's active permission grants through <see cref="IUserPermissionCache"/>.
/// </summary>
/// <remarks>
/// Lets a service deciding whether to offer a tool ask the same question the route filters ask, so the two
/// answers cannot diverge.
/// </remarks>
public interface IUserGrantReader
{
    /// <summary>Returns the permission ids the user (by Entra object id) actively holds.</summary>
    /// <returns>The user's grants; empty means no grants, not a miss.</returns>
    Task<IReadOnlySet<Guid>> GetGrantsAsync(Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class UserGrantReader(
    IUserPermissionCache cache,
    IServiceScopeFactory scopeFactory) : IUserGrantReader
{
    private readonly IUserPermissionCache _cache = cache;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <inheritdoc />
    public Task<IReadOnlySet<Guid>> GetGrantsAsync(Guid userId, CancellationToken cancellationToken) =>
        _cache.GetOrLoadAsync(userId, token => LoadAsync(userId, token), cancellationToken);

    #region Private methods

    // Its own scope: the loader is shared with concurrent waiters and can outlive the request that started
    // it, whose scoped context would already be disposed.
    private async Task<IReadOnlyCollection<Guid>> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        // User.Id is the Entra object id, so the grant table is queried directly with no join.
        return await ctx.UserPermissions
            .AsNoTracking()
            .Where(up => up.UserId == userId)
            .Select(up => up.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    #endregion
}
