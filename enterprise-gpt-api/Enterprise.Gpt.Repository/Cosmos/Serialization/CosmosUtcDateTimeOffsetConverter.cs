using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enterprise.Gpt.Service.Serialization;

/// <summary>
/// Writes every <see cref="DateTimeOffset"/> stored in Cosmos DB as a UTC instant in a
/// fixed-width ISO-8601 form, so that ordering the values as strings orders them chronologically.
/// </summary>
/// <remarks>
/// <para>
/// This is a correctness requirement of the transcript, not a formatting preference. Cosmos has no
/// date type: <c>ORDER BY c.dateCreated</c> is a lexicographic comparison of whatever strings were
/// stored. Lexicographic order equals chronological order only while every value shares one
/// offset and one width, and the default round-trip format satisfies neither — it preserves the
/// original offset (<c>+02:00</c> sorts after <c>Z</c> for an earlier instant) and it trims
/// trailing zeros from the fractional part (<c>…:00.5Z</c> sorts after <c>…:00.4999999Z</c>, but
/// also after <c>…:00.45Z</c>, which is earlier). Normalizing to UTC and padding to seven
/// fractional digits removes both.
/// </para>
/// <para>
/// Reading is deliberately permissive: any offset and any precision parses, because the value on
/// the way in may predate this converter. Only the write side is pinned.
/// </para>
/// </remarks>
public sealed class CosmosUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>
    /// The fixed-width UTC format every stored value is written in.
    /// </summary>
    /// <remarks>
    /// Seven fractional digits is <see cref="DateTime"/>'s full tick resolution, so no value is
    /// rounded on the way out and two instants one tick apart stay distinguishable — which is what
    /// the strict-monotonic clamp between a turn's two message timestamps relies on.
    /// </remarks>
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <inheritdoc />
    /// <exception cref="JsonException">
    /// Thrown when the value is not a string, or is a string that is not a valid ISO-8601 instant.
    /// </exception>
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
