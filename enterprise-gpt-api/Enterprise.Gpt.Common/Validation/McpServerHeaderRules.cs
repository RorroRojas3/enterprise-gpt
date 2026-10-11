using System.Collections.Frozen;
using System.Text.Json;

namespace Enterprise.Gpt.Common.Validation;

/// <summary>
/// The rules both MCP server action validators apply to the configured request headers, and the
/// limits the <c>McpServer.Headers</c> column is sized from.
/// </summary>
/// <remarks>
/// Non-secret configuration that selects which tools a server exposes, not a revived <c>HeaderKey</c> auth
/// type. It cannot become a secret store: <see cref="IsReserved"/> refuses auth and transport headers (and
/// <c>McpToolProvider</c> re-checks), values show in plain text to administrators, and the set is bounded.
/// </remarks>
public static class McpServerHeaderRules
{
    public const int MaxCount = 8;

    public const int MaxNameLength = 64;

    public const int MaxValueLength = 256;

    /// <summary>
    /// Budget for the serialized JSON and the width of its column; <see cref="SerializedLength"/> is the
    /// only measurement of it.
    /// </summary>
    public const int MaxSerializedLength = 1024;

    /// <summary>
    /// The one serializer for these headers, shared with the EF value converter so the length validated is
    /// the length persisted.
    /// </summary>
    /// <remarks>
    /// Frozen because it is public: another assembly setting <c>WriteIndented</c> or swapping the encoder
    /// would desynchronize the measurement from what is stored.
    /// </remarks>
    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// Headers a registration may not set, because something else already owns them.
    /// </summary>
    /// <remarks>
    /// Auth and connection headers belong to the on-behalf-of flow and the transport, where for example
    /// <c>Connection: close</c> would defeat pooling for every user. The MCP SDK (2.2.0) appends stored
    /// headers after its own, so a stored <c>Mcp-Session-Id</c> would ship twice. Content headers make the
    /// SDK throw <see cref="InvalidOperationException"/> while connecting, breaking every user's tools.
    /// </remarks>
    private static readonly FrozenSet<string> _reservedNames = new[]
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "Host",
        "Accept",
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "TE",
        "Trailer",
        "Upgrade",
        "Expect",

        "Mcp-Session-Id",
        "MCP-Protocol-Version",
        "Last-Event-ID",

        "Allow",
        "Content-Disposition",
        "Content-Encoding",
        "Content-Language",
        "Content-Length",
        "Content-Location",
        "Content-MD5",
        "Content-Range",
        "Content-Type",
        "Expires",
        "Last-Modified"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    #region Private methods

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        // The parameterless overload throws on hand-built options with no TypeInfoResolver; this one
        // installs the default reflection resolver.
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    #endregion

    #region Public static methods

    /// <summary>
    /// The length the given headers occupy once stored.
    /// </summary>
    public static int SerializedLength(IReadOnlyDictionary<string, string> headers)
    {
        return JsonSerializer.Serialize(headers, SerializerOptions).Length;
    }

    /// <summary>
    /// Whether a header name, matched case-insensitively, is one the API sets itself.
    /// </summary>
    public static bool IsReserved(string name)
    {
        return _reservedNames.Contains(name);
    }

    /// <summary>
    /// Validates a configured header set, yielding one message per problem found.
    /// </summary>
    public static IEnumerable<string> Validate(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            yield break;
        }

        if (headers.Count > MaxCount)
        {
            // Stop here: a huge payload would otherwise yield a failure per header and re-serialize the set.
            yield return $"At most {MaxCount} headers can be configured.";
            yield break;
        }

        // `X-Mcp-Tools` and `X-MCP-TOOLS` deserialize to two entries but are one header on the wire.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            if (!IsWellFormedName(name))
            {
                yield return $"Header names must be {MaxNameLength} characters or fewer of letters, digits and hyphens.";
                continue;
            }

            if (IsReserved(name))
            {
                yield return $"The '{name}' header is set by the API and cannot be configured here.";
                continue;
            }

            if (!seen.Add(name))
            {
                yield return $"The '{name}' header is listed more than once.";
                continue;
            }

            if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
            {
                yield return $"The value of '{name}' must be between 1 and {MaxValueLength} characters.";
                continue;
            }

            if (!IsWellFormedValue(value))
            {
                yield return $"The value of '{name}' must be printable ASCII with no line breaks.";
            }
        }

        if (SerializedLength(headers) > MaxSerializedLength)
        {
            yield return $"The configured headers are too large; they must fit in {MaxSerializedLength} characters once stored.";
        }
    }

    /// <summary>
    /// Whether a header value is non-empty printable ASCII within <see cref="MaxValueLength"/>.
    /// </summary>
    /// <remarks>
    /// The CRLF-injection guard: a line break splits one header into several on the wire. Printable ASCII
    /// because nothing sent needs more and non-ASCII header values have no agreed encoding.
    /// </remarks>
    public static bool IsWellFormedValue(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Length <= MaxValueLength
            && value.All(c => c is >= ' ' and <= '~');
    }

    /// <summary>
    /// Whether a header name is a non-empty run of ASCII letters, digits and hyphens within
    /// <see cref="MaxNameLength"/>.
    /// </summary>
    /// <remarks>
    /// Narrower than RFC 9110's <c>token</c> so a name cannot carry anything that reads as structure.
    /// </remarks>
    public static bool IsWellFormedName(string name)
    {
        return !string.IsNullOrEmpty(name)
            && name.Length <= MaxNameLength
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    #endregion
}
