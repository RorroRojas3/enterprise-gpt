using Enterprise.Gpt.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Service.Caching;

/// <summary>
/// Reads a user's active permission grants through <see cref="IUserPermissionCache"/>.
/// </summary>
/// <remarks>
/// Exists because a permission check is no longer only an endpoint concern: a service deciding
/// whether to offer the model a tool has to ask the same question the route filters ask, and asking
/// it a second way is how two answers start to disagree.
/// </remarks>
public interface IUserGrantReader
{
    /// <summary>
    /// Returns the permission ids the user actively holds.
    /// </summary>
    /// <param name="userId">The user's Entra object id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>
    /// The user's grants. Empty is a real answer — a user with no grants at all — not a miss.
    /// </returns>
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

    /// <summary>
    /// Reads the caller's full active grant set on a cache miss.
    /// </summary>
    /// <remarks>
    /// The loader creates its own scope rather than closing over this instance's context. One loader
    /// is shared with every concurrent caller waiting on the same user, so it can outlive the
    /// request that happened to start it — and a captured scoped <see cref="EnterpriseGptDbContext"/>
    /// would already be disposed by the time a later waiter's continuation ran.
    /// </remarks>
    private async Task<IReadOnlyCollection<Guid>> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        // User.Id is the Entra object id, so the grant table is queried directly with no join.
        return await ctx.UserPermissions
            .AsNoTracking()
            .Where(up => up.UserId == userId && !up.DateDeactivated.HasValue)
            .Select(up => up.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
