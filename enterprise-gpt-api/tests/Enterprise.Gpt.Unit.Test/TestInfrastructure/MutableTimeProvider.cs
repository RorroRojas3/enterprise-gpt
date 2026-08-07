namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it, so expiry behaviour
/// can be asserted without wall-clock delays.
/// </summary>
public sealed class MutableTimeProvider : TimeProvider
{
    /// <summary>
    /// The instant <see cref="GetUtcNow"/> returns. Assign or <see cref="Advance"/> to move it.
    /// </summary>
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="delta"/>.
    /// </summary>
    /// <param name="delta">How far to advance.</param>
    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
    }
}
