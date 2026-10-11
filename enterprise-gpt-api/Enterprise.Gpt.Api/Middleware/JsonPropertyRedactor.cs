using System.Text.Json;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Scrubs sensitive content out of a captured HTTP payload before it is logged.
/// </summary>
/// <remarks>
/// What a log record may contain has to be decided before it exists, which <see cref="ILogger{TCategoryName}"/>
/// cannot do. Replace the default registration to redact by a different rule.
/// </remarks>
public interface IRequestBodyRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="body"/>, possibly truncated at the byte cap, that is safe to log.
    /// </summary>
    string Redact(string body, string? contentType);
}

/// <summary>
/// Default <see cref="IRequestBodyRedactor"/>: replaces the value of every JSON property named in
/// <see cref="BodyLoggingOptions.RedactedPropertyNames"/> with a marker.
/// </summary>
public sealed class JsonPropertyRedactor(IOptions<RequestLoggingOptions> options) : IRequestBodyRedactor
{
    /// <summary>The value substituted for a redacted property.</summary>
    public const string RedactionMarker = "[redacted]";

    internal const string UnparseableMarker = "[redacted: unparseable payload naming a sensitive property]";

    // Normalized so accessToken, access_token and access-token are one name: OAuth and OIDC payloads are
    // snake_case while this API is camelCase.
    private readonly HashSet<string> _redactedNames =
        new(options.Value.Bodies.RedactedPropertyNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Redact(string body, string? contentType)
    {
        if (string.IsNullOrEmpty(body) || _redactedNames.Count == 0)
        {
            return body;
        }

        if (!IsJson(contentType))
        {
            return ScrubUnstructured(body);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            using var buffer = new MemoryStream(body.Length);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(writer, document.RootElement);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (JsonException)
        {
            // Expected: a payload truncated at the byte cap is nearly always invalid JSON.
            return ScrubUnstructured(body);
        }
    }

    #region Private methods

    private void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (_redactedNames.Contains(Normalize(property.Name)))
                    {
                        writer.WriteString(property.Name, RedactionMarker);
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    // All-or-nothing: masking in place would need a parser for content that has no structure to walk.
    private string ScrubUnstructured(string body)
    {
        // Normalized like the structured path: this arm is common, and camelCase-only matching would let a
        // truncated {"api_key":"sk-live-… through in full.
        var haystack = Normalize(body);

        return _redactedNames.Any(name => haystack.Contains(name, StringComparison.OrdinalIgnoreCase))
            ? UnparseableMarker
            : body;
    }

    private static string Normalize(string value) =>
        value.Contains('_') || value.Contains('-')
            ? value.Replace("_", string.Empty).Replace("-", string.Empty)
            : value;

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.Contains("/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));

    #endregion
}
