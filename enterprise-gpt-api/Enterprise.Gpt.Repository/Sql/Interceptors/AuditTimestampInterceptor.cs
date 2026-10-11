using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enterprise.Gpt.Repository.Sql.Interceptors;

/// <summary>
/// Stamps <see cref="IBaseCreatedEntity.DateCreated"/> on insert and <see cref="IBaseModifiedEntity.DateModified"/>
/// on insert and update, from one <see cref="TimeProvider"/> reading per save.
/// </summary>
/// <remarks>
/// <c>ExecuteUpdateAsync</c> bypasses the change tracker, so a set-based update stamps its own
/// <c>DateModified</c>.
/// </remarks>
internal sealed class AuditTimestampInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    #region Private methods

    private void Stamp(DbContext? ctx)
    {
        if (ctx is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var entry in ctx.ChangeTracker.Entries<IBaseCreatedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.DateCreated = now;
                    if (entry.Entity is IBaseModifiedEntity added)
                    {
                        added.DateModified = now;
                    }

                    break;
                case EntityState.Modified when entry.Entity is IBaseModifiedEntity modified:
                    modified.DateModified = now;
                    break;
            }
        }
    }

    #endregion
}
