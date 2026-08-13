using System.Diagnostics.Metrics;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Creates plain meters for tests that construct a metrics type but do not read what it records.
/// </summary>
/// <remarks>
/// Instruments with no listener attached are inert, so a test that only needs the type to exist
/// pays nothing for it. Tests that do assert on measurements use a real
/// <see cref="MeterListener"/> against the meters this hands out.
/// </remarks>
public sealed class DummyMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    /// <inheritdoc />
    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);

        return meter;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }
}
