using System.Text.Json;
using Enterprise.Gpt.Service.Serialization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Serialization;

/// <summary>
/// These are correctness tests for transcript ordering, not formatting tests. Cosmos DB has no date
/// type, so <c>ORDER BY c.dateCreated</c> compares the stored strings lexicographically; if that
/// order can disagree with chronological order for any pair of values, a conversation can render
/// its answer before its question and replay the wrong history to the model on every later turn.
/// </summary>
public sealed class CosmosUtcDateTimeOffsetConverterTests
{
    private static readonly JsonSerializerOptions Options = CosmosSerialization.CreateOptions();

    /// <summary>
    /// Values chosen so that a naive round-trip format would sort them wrongly: the second and
    /// fourth carry non-zero offsets whose local time reads later than an instant that is actually
    /// earlier, and the fractional parts differ in significant-digit count.
    /// </summary>
    private static readonly DateTimeOffset[] MixedValues =
    [
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        new(2026, 8, 15, 13, 30, 0, TimeSpan.FromHours(5)),
        new(2026, 8, 15, 11, 59, 59, 999, TimeSpan.Zero),
        new(2026, 8, 15, 6, 0, 0, TimeSpan.FromHours(-7)),
        new(2026, 8, 15, 12, 0, 0, 450, TimeSpan.Zero),
        new(2026, 8, 15, 12, 0, 0, 500, TimeSpan.Zero),
        new(2026, 8, 15, 12, 0, 1, TimeSpan.Zero)
    ];

    [Fact]
    public void Write_NormalizesToUtc()
    {
        var value = new DateTimeOffset(2026, 8, 15, 14, 0, 0, TimeSpan.FromHours(2));

        var json = JsonSerializer.Serialize(value, Options);

        Assert.Equal("\"2026-08-15T12:00:00.0000000Z\"", json);
    }

    [Fact]
    public void Write_PadsTheFractionalSecondToFullTickResolution()
    {
        var value = new DateTimeOffset(2026, 8, 15, 12, 0, 0, 500, TimeSpan.Zero);

        var json = JsonSerializer.Serialize(value, Options);

        Assert.Equal("\"2026-08-15T12:00:00.5000000Z\"", json);
    }

    [Fact]
    public void Write_ValuesDifferingInOffsetAndPrecision_ProduceEqualWidthStrings()
    {
        var widths = MixedValues.Select(x => JsonSerializer.Serialize(x, Options).Length).Distinct();

        Assert.Single(widths);
    }

    [Fact]
    public void Write_ValuesDifferingInOffsetAndPrecision_SortAsStringsInChronologicalOrder()
    {
        var byString = MixedValues
            .OrderBy(x => JsonSerializer.Serialize(x, Options), StringComparer.Ordinal)
            .ToList();

        var byInstant = MixedValues
            .OrderBy(x => x.UtcTicks)
            .ToList();

        Assert.Equal(byInstant, byString);
    }

    [Fact]
    public void Write_TwoInstantsOneTickApart_StayDistinguishable()
    {
        var earlier = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var later = earlier.AddTicks(1);

        var earlierJson = JsonSerializer.Serialize(earlier, Options);
        var laterJson = JsonSerializer.Serialize(later, Options);

        Assert.NotEqual(earlierJson, laterJson);
        Assert.True(string.CompareOrdinal(earlierJson, laterJson) < 0);
    }

    [Theory]
    [InlineData("\"2026-08-15T12:00:00.0000000Z\"")]
    [InlineData("\"2026-08-15T14:00:00+02:00\"")]
    [InlineData("\"2026-08-15T12:00:00Z\"")]
    [InlineData("\"2026-08-15T12:00:00.5Z\"")]
    public void Read_AcceptsAnyIsoOffsetAndPrecision(string json)
    {
        var value = JsonSerializer.Deserialize<DateTimeOffset>(json, Options);

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTime(2026, 8, 15), value.Date);
    }

    [Fact]
    public void Read_RoundTripsWhatWasWritten()
    {
        foreach (var expected in MixedValues)
        {
            var json = JsonSerializer.Serialize(expected, Options);

            var actual = JsonSerializer.Deserialize<DateTimeOffset>(json, Options);

            Assert.Equal(expected.UtcTicks, actual.UtcTicks);
        }
    }

    [Fact]
    public void Read_ValueThatIsNotAnInstant_Throws()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>("\"not-a-date\"", Options));
    }

    [Fact]
    public void Read_ValueThatIsNotAString_Throws()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>("1755259200", Options));
    }
}
