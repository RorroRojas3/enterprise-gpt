using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enterprise.Gpt.Repository.Sql.Interceptors;

/// <summary>
/// Turns a tracked delete of any <see cref="IBaseEntity"/> into an update that stamps
/// <see cref="IBaseEntity.DateDeactivated"/>.
/// </summary>
/// <remarks>
/// <c>ExecuteDeleteAsync</c> bypasses the change tracker, so a set-based delete is still a hard delete.
/// </remarks>
internal sealed class SoftDeleteInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        SoftDelete(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SoftDelete(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    #region Private methods

    private void SoftDelete(DbContext? ctx)
    {
        if (ctx is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        // Materialized first: changing an entry's state while enumerating the tracker is not supported.
        foreach (var entry in ctx.ChangeTracker.Entries<IBaseEntity>().Where(e => e.State == EntityState.Deleted).ToList())
        {
            entry.State = EntityState.Modified;
            entry.Entity.DateDeactivated = now;
        }
    }

    #endregion
}
