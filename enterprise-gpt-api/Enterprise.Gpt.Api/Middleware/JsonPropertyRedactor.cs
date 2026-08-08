using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Scrubs sensitive content out of a captured HTTP payload before it is logged.
/// </summary>
/// <remarks>
/// The seam <see cref="ILogger{TCategoryName}"/> cannot provide. Which backend receives a log record is a
/// hosting concern; what the record is allowed to contain is not, and has to be decided before the record
/// exists. Replace the default registration to redact by a different rule.
/// </remarks>
public interface IRequestBodyRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="body"/> safe to log.
    /// </summary>
    /// <param name="body">The captured payload, already truncated to the configured cap.</param>
    /// <param name="contentType">The declared content type, or <see langword="null"/> if absent.</param>
    /// <returns>The redacted payload. Never <see langword="null"/>.</returns>
    /// <example>
    /// <code>
    /// redactor.Redact("""{"name":"acme","apiKey":"sk-live-1"}""", "application/json");
    /// // {"name":"acme","apiKey":"[redacted]"}
    /// </code>
    /// </example>
    string Redact(string body, string? contentType);
}

/// <summary>
/// Default <see cref="IRequestBodyRedactor"/>: rewrites a JSON payload, replacing the value of every
/// property named in <see cref="BodyLoggingOptions.RedactedPropertyNames"/> with a marker.
/// </summary>
/// <param name="options">Supplies the property names to redact.</param>
public sealed class JsonPropertyRedactor(IOptions<RequestLoggingOptions> options) : IRequestBodyRedactor
{
    /// <summary>The value substituted for a redacted property.</summary>
    public const string RedactionMarker = "[redacted]";

    /// <summary>Replaces a payload that could not be parsed but does mention a redacted name.</summary>
    internal const string UnparseableMarker = "[redacted: unparseable payload naming a sensitive property]";

    // Normalized so accessToken, access_token and access-token are one name. OAuth and OIDC payloads are
    // snake_case while the rest of this API is camelCase, and a list that had to spell out both would
    // quietly miss whichever spelling nobody thought of.
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
            // Expected rather than exceptional: a payload truncated at the byte cap is nearly always
            // invalid JSON. Falling back to the raw text would defeat the point, so anything naming a
            // sensitive property is dropped whole.
            return ScrubUnstructured(body);
        }
    }

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

    /// <summary>
    /// All-or-nothing fallback for content with no structure to walk. Masking in place would need a
    /// parser for a format that is, by definition, not parseable here.
    /// </summary>
    private string ScrubUnstructured(string body)
    {
        // Normalized on both sides, exactly as the structured path is. This arm is not a rare one — a
        // payload cut at the byte cap is nearly always invalid JSON — so a fallback that only recognised
        // camelCase would let a truncated {"api_key":"sk-live-… through in full.
        var haystack = Normalize(body);

        return _redactedNames.Any(name => haystack.Contains(name, StringComparison.OrdinalIgnoreCase))
            ? UnparseableMarker
            : body;
    }

    /// <summary>
    /// Folds text to the form both spellings share, so <c>access_token</c> and <c>accessToken</c> compare
    /// equal.
    /// </summary>
    private static string Normalize(string value) =>
        value.Contains('_') || value.Contains('-')
            ? value.Replace("_", string.Empty).Replace("-", string.Empty)
            : value;

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (contentType.Contains("/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase));
}
