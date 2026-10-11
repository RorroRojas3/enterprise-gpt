using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Fails the first save that writes a <typeparamref name="T"/> in <paramref name="state"/>, the way a lost
/// row-version race does on SQL Server, which SQLite cannot reproduce with the token neutralized.
/// </summary>
internal sealed class LostRaceInterceptor<T>(EntityState state = EntityState.Modified) : SaveChangesInterceptor
    where T : class
{
    private readonly EntityState _state = state;
    private int _armed = 1;

    public int Fired { get; private set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<T>().Any(e => e.State == _state)
            && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Fired++;
            throw new DbUpdateConcurrencyException($"Simulated lost race on {typeof(T).Name}.", []);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
