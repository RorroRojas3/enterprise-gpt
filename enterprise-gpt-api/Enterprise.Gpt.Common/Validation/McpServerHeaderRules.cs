using System.Collections.Frozen;
using System.Text.Json;

namespace Enterprise.Gpt.Dto.Actions.Mcp;

/// <summary>
/// The rules both MCP server action validators apply to the configured request headers, and the
/// limits the <c>McpServer.Headers</c> column is sized from.
/// </summary>
/// <remarks>
/// Shared rather than duplicated into each validator the way the URL and icon rules are: this set
/// is long enough that two copies would drift, and <c>McpServerConfiguration</c> reads
/// <see cref="MaxSerializedLength"/> so the column and the validator cannot disagree about the
/// budget.
///
/// These headers are configuration, not credentials, and this is **not** a reopening of the
/// refused <c>HeaderKey</c> auth type. That refusal was about an authentication *mechanism* whose
/// value is a bearer-equivalent secret; <c>McpAuthTypes</c> still has its two members and the auth
/// type select still offers two options. What is admitted here is orthogonal: non-secret headers
/// that select which tools a server exposes.
///
/// Three properties keep it from becoming a covert secret store, none of them merely a policy
/// statement. <see cref="IsReserved"/> refuses every header the transport and the on-behalf-of
/// flow own, and <c>McpToolProvider</c> applies the same rules again on the read path. Values
/// round-trip to <c>GET api/mcps/all</c> and render in plain text in the admin dialog, so nothing
/// stored here is concealed from the next administrator to open the row. And the set is
/// arithmetically bounded — <see cref="MaxCount"/> names of <see cref="MaxNameLength"/> and values
/// of <see cref="MaxValueLength"/> cannot grow into a blob.
/// </remarks>
public static class McpServerHeaderRules
{
    /// <summary>Most headers one server may carry.</summary>
    public const int MaxCount = 8;

    /// <summary>Longest permitted header name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Longest permitted header value.</summary>
    public const int MaxValueLength = 256;

    /// <summary>
    /// Budget for the serialized JSON, and the width of the column that stores it. The column is
    /// sized from this constant and <see cref="SerializedLength"/> is the only measurement of it,
    /// so the two cannot disagree about what fits.
    /// </summary>
    public const int MaxSerializedLength = 1024;

    /// <summary>
    /// The one serializer for these headers, shared with the EF value converter so the length
    /// measured during validation is the length persisted. A second options instance — even an
    /// identically configured one — is what would let an accepted set fail to save.
    /// </summary>
    /// <remarks>
    /// Frozen at initialization rather than left mutable: a <see cref="JsonSerializerOptions"/> is
    /// writable until its first use, and this one is public, so without this any assembly could
    /// set <c>WriteIndented</c> or swap the encoder and desynchronize the measurement from what is
    /// written to an <c>nvarchar</c> column. The invariant is enforced, not just described.
    /// </remarks>
    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        // `populateMissingResolver`, because the parameterless overload throws when
        // `TypeInfoResolver` is unset — which it is here, these options being hand-built rather
        // than taken from a configured pipeline. It installs the default reflection resolver,
        // which is what serializing this dictionary would have used anyway.
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

    /// <summary>
    /// Headers a registration may not set, because something else already owns them.
    /// </summary>
    /// <remarks>
    /// Three groups, and the reasons differ.
    ///
    /// <c>Authorization</c> is the on-behalf-of bearer; the rest of the first group are the
    /// connection's own. <c>McpToolProvider</c> writes the bearer last as a second lock, which
    /// covers that one name even for a row written before these rules.
    ///
    /// The transport-control headers in that group are the quiet ones. <c>HttpRequestHeaders</c>
    /// *accepts* <c>Connection</c>, <c>Transfer-Encoding</c> and <c>Expect</c> and binds them to
    /// the typed properties <c>SocketsHttpHandler</c> reads, so — unlike a content header — they
    /// do not throw. They just change how the connection behaves for every user of the server:
    /// <c>Connection: close</c> defeats pooling on a session meant to be long-lived, and
    /// <c>Expect: 100-continue</c> adds a round trip that stalls against a server that ignores it.
    ///
    /// The MCP protocol headers are the group that lock cannot cover.
    /// <c>StreamableHttpClientSessionTransport.CopyAdditionalHeaders</c> in ModelContextProtocol
    /// 2.2.0 sets <c>Mcp-Session-Id</c>, <c>MCP-Protocol-Version</c> and <c>Last-Event-ID</c>
    /// first and then copies <c>AdditionalHeaders</c> with <c>TryAddWithoutValidation</c>, which
    /// **appends** rather than replaces — so a stored <c>Mcp-Session-Id</c> would ship alongside
    /// the real one on every request from every user of the server. The collision happens inside
    /// <c>HttpRequestHeaders</c>, not inside our dictionary, so ordering cannot defend it.
    ///
    /// The content headers are refused because <c>HttpRequestHeaders.TryAddWithoutValidation</c>
    /// returns <see langword="false"/> for every member of <c>HttpContentHeaders</c>, and the SDK
    /// turns that into an <see cref="InvalidOperationException"/> while connecting. Reserving the
    /// whole set is what keeps an administrator from breaking tool acquisition for every user of
    /// a server with one plausible-looking entry.
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

    /// <summary>
    /// The length the given headers occupy once stored.
    /// </summary>
    /// <param name="headers">The headers to measure.</param>
    /// <returns>The serialized character count.</returns>
    public static int SerializedLength(IReadOnlyDictionary<string, string> headers)
    {
        return JsonSerializer.Serialize(headers, SerializerOptions).Length;
    }

    /// <summary>
    /// Whether a header name is one the API sets itself and a registration may not override.
    /// </summary>
    /// <param name="name">The header name to test, matched case-insensitively.</param>
    /// <returns><see langword="true"/> when the name is reserved.</returns>
    public static bool IsReserved(string name)
    {
        return _reservedNames.Contains(name);
    }

    /// <summary>
    /// Validates a configured header set, yielding one message per problem found.
    /// </summary>
    /// <param name="headers">The headers as the request carried them; may be <see langword="null"/>.</param>
    /// <returns>The reasons the set is unusable, empty when it is valid.</returns>
    /// <example>
    /// <code>
    /// RuleFor(x =&gt; x.Headers).Custom((headers, context) =&gt;
    /// {
    ///     foreach (var message in McpServerHeaderRules.Validate(headers))
    ///     {
    ///         context.AddFailure(message);
    ///     }
    /// });
    /// </code>
    /// </example>
    public static IEnumerable<string> Validate(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            yield break;
        }

        if (headers.Count > MaxCount)
        {
            // Stop here rather than reporting on every entry as well: a huge payload would
            // otherwise produce a failure per header and then re-serialize the whole set, and
            // the count is the one thing the reader has to fix first anyway.
            yield return $"At most {MaxCount} headers can be configured.";
            yield break;
        }

        // Case-insensitive, because HTTP header names are: a payload carrying both `X-Mcp-Tools`
        // and `X-MCP-TOOLS` deserializes to two entries that are one header on the wire, and
        // which of them survived would be an ordering accident.
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
    /// <param name="value">The header value to test.</param>
    /// <returns><see langword="true"/> when the value is well formed.</returns>
    /// <remarks>
    /// This is the CRLF-injection guard and is not optional: a carriage return or line feed in a
    /// value splits one header into several on the wire. The rule is drawn at printable ASCII
    /// rather than at "no control characters" because nothing this system sends needs more, and a
    /// non-ASCII value has no agreed encoding on an HTTP header anyway.
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
    /// <param name="name">The header name to test.</param>
    /// <returns><see langword="true"/> when the name is well formed.</returns>
    /// <remarks>
    /// Narrower than RFC 9110's <c>token</c> production, which also admits <c>!#$%&amp;'*+.^_`|~</c>.
    /// Nothing in this system needs those, and the narrow rule is what keeps a name from carrying
    /// anything that reads as structure.
    /// </remarks>
    public static bool IsWellFormedName(string name)
    {
        return !string.IsNullOrEmpty(name)
            && name.Length <= MaxNameLength
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
