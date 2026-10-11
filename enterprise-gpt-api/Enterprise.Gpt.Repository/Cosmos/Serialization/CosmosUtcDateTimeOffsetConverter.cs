using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Repository.Cosmos.Serialization;

/// <summary>
/// Writes every stored <see cref="DateTimeOffset"/> as fixed-width UTC ISO-8601, so string order is
/// chronological order.
/// </summary>
/// <remarks>
/// Cosmos has no date type, so <c>ORDER BY c.dateCreated</c> compares strings. Reads accept any offset and
/// precision because stored values may predate this converter.
/// </remarks>
public sealed class CosmosUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The fixed-width UTC format every stored value is written in.</summary>
    /// <remarks>
    /// Full tick resolution, so instants one tick apart stay distinct; the clamp between a turn's two
    /// message timestamps relies on it.
    /// </remarks>
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <inheritdoc />
    /// <exception cref="JsonException">The value is not a string or not a valid ISO-8601 instant.</exception>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"Expected a string to read a {nameof(DateTimeOffset)}, found {reader.TokenType}.");
        }

        var value = reader.GetString();

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : throw new JsonException($"'{value}' is not a valid ISO-8601 instant.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
    }
}
