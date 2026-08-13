using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Diagnostics.Metrics;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Observability;

public sealed class ChatMetricsTests : IDisposable
{
    private readonly DummyMeterFactory _meterFactory = new();
    private readonly ChatMetrics _metrics;
    private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly MeterListener _listener = new();

    public ChatMetricsTests()
    {
        _metrics = new ChatMetrics(_meterFactory);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ChatMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(Record);
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) => Record(instrument, measurement, tags, state));
        _listener.Start();
    }

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct
    {
        _measurements.Add((instrument.Name, Convert.ToDouble(measurement), tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
        _meterFactory.Dispose();
    }

    [Fact]
    public void RecordEstimateRelativeError_Overestimate_RecordsASignedValue()
    {
        _metrics.RecordEstimateRelativeError(0.12, Guid.Empty, "gpt-test");

        var measurement = Assert.Single(_measurements, entry => entry.Instrument.EndsWith("relative_error", StringComparison.Ordinal));
        Assert.Equal(0.12, measurement.Value, precision: 5);
    }

    /// <summary>
    /// The sign is what separates a systematic undercount from noise around zero, so it survives.
    /// </summary>
    [Fact]
    public void RecordEstimateRelativeError_Underestimate_KeepsTheNegativeSign()
    {
        _metrics.RecordEstimateRelativeError(-0.2, Guid.Empty, "gpt-test");

        var measurement = Assert.Single(_measurements, entry => entry.Instrument.EndsWith("relative_error", StringComparison.Ordinal));
        Assert.True(measurement.Value < 0);
    }

    /// <summary>
    /// Metrics are retained and queried far more widely than logs, so no dimension may carry a
    /// conversation, a user, or message content.
    /// </summary>
    [Fact]
    public void RecordEstimateRelativeError_Tags_CarryOnlyTheProviderAndDeployment()
    {
        _metrics.RecordEstimateRelativeError(0.05, Guid.NewGuid(), "gpt-test");

        var measurement = Assert.Single(_measurements);
        Assert.Equal(["deployment.name", "provider.id"], measurement.Tags.Keys.Order());
    }

    [Fact]
    public void RecordBudgetDrop_Drops_RecordsBothCountersTaggedWithTheDeploymentOnly()
    {
        _metrics.RecordBudgetDrop(3, 1_200, "gpt-test");

        var messages = Assert.Single(_measurements, entry => entry.Instrument.EndsWith("dropped_messages", StringComparison.Ordinal));
        var tokens = Assert.Single(_measurements, entry => entry.Instrument.EndsWith("dropped_tokens", StringComparison.Ordinal));

        Assert.Equal(3, messages.Value);
        Assert.Equal(1_200, tokens.Value);
        Assert.Equal("deployment.name", Assert.Single(tokens.Tags).Key);
    }
}
