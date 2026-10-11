namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it, so expiry behaviour
/// can be asserted without wall-clock delays.
/// </summary>
public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        return UtcNow;
    }

    /// <summary>
    /// Ticks of <see cref="UtcNow"/>, so <see cref="TimeProvider.GetElapsedTime(long)"/> measures the
    /// test's clock rather than the wall clock.
    /// </summary>
    public override long GetTimestamp()
    {
        return UtcNow.Ticks;
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
    }
}
