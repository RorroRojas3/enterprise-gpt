using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Tuning knobs for <see cref="RequestLoggingMiddleware"/>, bound from the <c>RequestLogging</c>
/// configuration section and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// The middleware writes through <see cref="ILogger{TCategoryName}"/> only, so which backend the records
/// land in — Application Insights, Serilog, the console — is a hosting concern these options say nothing
/// about. What they do control is the one thing the backend cannot: how much of a request is allowed to
/// leave the process in the first place.
/// </para>
/// <para>
/// Every list property defaults to empty and is filled in by <see cref="ApplyListDefaults"/> after
/// binding, rather than being initialised inline. <c>ConfigurationBinder</c> binds a collection
/// <em>into</em> the existing instance instead of replacing it, so an inline default would make
/// configuration purely additive — an operator narrowing <see cref="BodyLoggingOptions.AllowedContentTypes"/>
/// to one media type would silently keep all the others, which for an allow-list is the wrong direction
/// to fail in.
/// </para>
/// </remarks>
public sealed class RequestLoggingOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "RequestLogging";

    private static readonly string[] _defaultExcludedPaths = ["/health", "/openapi", "/scalar"];

    // "dir" and "permissionId" join the paging keys because neither identifies a person: one is
    // 'asc' or 'desc', the other a permission's id. Redacting them would leave the access log unable
    // to explain the two rejections the list routes can now raise.
    private static readonly string[] _defaultSafeQueryKeys =
        ["skip", "take", "page", "pageSize", "sort", "order", "dir", "permissionId"];

    /// <summary>
    /// Whether request logging is active. Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Read once, at pipeline construction: when false the middleware is left out of the pipeline
    /// altogether rather than short-circuiting inside itself, so a disabled access log costs nothing per
    /// request. The trade-off is that toggling it requires a restart.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Level for the line written when a request arrives. Default: <see cref="LogLevel.Information"/>.
    /// </summary>
    /// <remarks>
    /// Lowering this to <see cref="LogLevel.Debug"/> roughly halves the volume, and loses little: the
    /// completion line carries a superset of these fields. It earns its place at Information for two
    /// cases the completion line cannot cover — a request that is still running (SSE streams live for
    /// minutes), and one whose process died before it finished.
    /// </remarks>
    public LogLevel RequestStartLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// How long a successful request may take before its completion line is raised to
    /// <see cref="LogLevel.Warning"/>. Default: 3 seconds.
    /// </summary>
    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Path prefixes whose successful requests are not logged at all. Matched with
    /// <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/>, case-insensitively.
    /// Defaults to <c>/health</c>, <c>/openapi</c> and <c>/scalar</c> when left unset.
    /// </summary>
    /// <remarks>
    /// Failures on these paths are still logged. Silencing a health probe is worth doing; silencing a
    /// health probe that has started returning 500 is not.
    /// </remarks>
    public IList<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Whether query string values are replaced with a redaction marker. Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// On by default because query values here are user content — <c>api/conversations/search?name=</c>
    /// carries whatever the user typed. Keys are always logged; only values are masked.
    /// </remarks>
    public bool RedactQueryValues { get; set; } = true;

    /// <summary>
    /// Query string keys whose values survive redaction, compared case-insensitively. Defaults to the
    /// pagination and ordering keys plus <c>permissionId</c> when left unset.
    /// </summary>
    public IList<string> SafeQueryKeys { get; set; } = [];

    /// <summary>
    /// Whether the <c>User-Agent</c> header is logged. Default: <see langword="true"/>.
    /// </summary>
    public bool IncludeUserAgent { get; set; } = true;

    /// <summary>
    /// Whether the caller's remote IP address is logged. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Off by default: an IP address is personal data under the GDPR, and the Entra object id already
    /// logged on the completion line identifies the caller far more usefully.
    /// </remarks>
    public bool IncludeClientIp { get; set; }

    /// <summary>
    /// Request and response body capture. Off by default.
    /// </summary>
    public BodyLoggingOptions Bodies { get; set; } = new();

    /// <summary>
    /// Fills in the default for every list left empty by configuration.
    /// </summary>
    /// <remarks>
    /// Runs as a post-configure step, after binding, so an explicitly configured list replaces the
    /// default rather than extending it. See the remarks on this type.
    /// </remarks>
    internal void ApplyListDefaults()
    {
        if (ExcludedPaths.Count == 0)
        {
            ExcludedPaths = [.. _defaultExcludedPaths];
        }

        if (SafeQueryKeys.Count == 0)
        {
            SafeQueryKeys = [.. _defaultSafeQueryKeys];
        }

        Bodies.ApplyListDefaults();
    }

    /// <summary>
    /// Validates the list contents that a <see cref="ValidationAttribute"/> cannot express.
    /// </summary>
    /// <returns><see langword="true"/> when every entry is usable.</returns>
    /// <remarks>
    /// Element-level validation matters because both this type and
    /// <see cref="JsonPropertyRedactor"/> build a <see cref="HashSet{T}"/> from these lists in a field
    /// initialiser. Those run when the middleware is first constructed — on the first request — so an
    /// entry that configuration bound as <see langword="null"/> would surface as a 500 per request rather
    /// than as a startup failure.
    /// </remarks>
    internal bool HasValidEntries() =>
        ExcludedPaths.All(IsRootedPath)
        && SafeQueryKeys.All(key => !string.IsNullOrWhiteSpace(key))
        && Bodies.HasValidEntries();

    internal static bool IsRootedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/');
}

/// <summary>
/// Controls whether — and how much of — HTTP payloads are logged, bound from the
/// <c>RequestLogging:Bodies</c> configuration section.
/// </summary>
/// <remarks>
/// Both switches default to off. This is a chat platform: request bodies carry user prompts and extracted
/// document text, so enabling capture puts user content into whatever telemetry backend is configured.
/// The remaining settings exist so that turning the switch on cannot break streaming, exhaust memory, or
/// ship a 50 MB upload to Application Insights.
/// </remarks>
public sealed class BodyLoggingOptions
{
    private static readonly string[] _defaultAllowedContentTypes =
        ["application/json", "application/problem+json", "text/plain"];

    // /api/conversations as a whole, not just the stream route: GET api/conversations/{id}/messages
    // returns the entire prompt-and-response transcript as application/json, and the list and search
    // routes return user-authored titles.
    private static readonly string[] _defaultExcludedPaths = ["/api/conversations", "/api/documents"];

    // downloadUrl is on the list because it is a credential, not a link: the signed document download
    // URL grants read access to anyone holding it until it expires. /api/documents is excluded from
    // capture already, so this only matters once an operator narrows ExcludedPaths.
    private static readonly string[] _defaultRedactedPropertyNames =
    [
        "password", "token", "accessToken", "refreshToken", "idToken", "secret",
        "apiKey", "clientSecret", "connectionString", "authorization", "downloadUrl"
    ];

    /// <summary>
    /// Whether eligible request bodies are captured. Default: <see langword="false"/>.
    /// </summary>
    public bool LogRequestBody { get; set; }

    /// <summary>
    /// Whether eligible response bodies are captured. Default: <see langword="false"/>.
    /// </summary>
    public bool LogResponseBody { get; set; }

    /// <summary>
    /// Level for the payload record. Default: <see cref="LogLevel.Information"/>.
    /// </summary>
    /// <remarks>
    /// Payloads are written as their own record rather than as fields on the completion line, so this
    /// level filters them independently. Setting it to <see cref="LogLevel.Debug"/> adds a second gate:
    /// capture then requires both the switch above and a category filter that admits Debug.
    /// </remarks>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Largest number of bytes captured from each body. Default: 4096.
    /// </summary>
    /// <remarks>
    /// Also the cutoff for whether a request body is read at all: a request declaring a larger
    /// <c>Content-Length</c> is skipped outright rather than truncated, because
    /// <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/> spools the entire body —
    /// to disk past its own threshold — regardless of how little of it is ever read.
    /// </remarks>
    [Range(1, 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 4096;

    /// <summary>
    /// Media types eligible for capture, compared case-insensitively against the content type with any
    /// parameters stripped. Any media type with a <c>+json</c> structured suffix is also eligible.
    /// Defaults to JSON, problem+JSON and plain text when left unset.
    /// </summary>
    /// <remarks>
    /// This allow-list is what keeps binary payloads out: <c>multipart/form-data</c> document uploads and
    /// <c>application/octet-stream</c> downloads are simply not on it. It is also the gate that abandons
    /// capture of a <c>text/event-stream</c> response once its content type becomes known.
    /// </remarks>
    public IList<string> AllowedContentTypes { get; set; } = [];

    /// <summary>
    /// Request paths excluded from capture entirely. A <c>*</c> matches exactly one path segment.
    /// Defaults to <c>/api/conversations</c> and <c>/api/documents</c> when left unset.
    /// </summary>
    /// <remarks>
    /// Route templates cannot be used here: capture has to be set up before <c>next()</c> runs, and
    /// routing has not yet resolved an endpoint at that point. The defaults cover the two route families
    /// that carry user content — conversations and document uploads — so the capture wrapper is never
    /// even installed on them; the content-type check is the second line of defence.
    /// </remarks>
    public IList<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Property names whose values are replaced with a redaction marker. Compared case-insensitively and
    /// ignoring <c>_</c> and <c>-</c>, so <c>accessToken</c> also matches <c>access_token</c>.
    /// </summary>
    /// <remarks>
    /// Applied by <see cref="IRequestBodyRedactor"/>. Register a different implementation into the
    /// container before <c>AddRequestLogging</c> runs to redact by some other rule.
    /// </remarks>
    public IList<string> RedactedPropertyNames { get; set; } = [];

    /// <summary>
    /// Fills in the default for every list left empty by configuration.
    /// </summary>
    internal void ApplyListDefaults()
    {
        if (AllowedContentTypes.Count == 0)
        {
            AllowedContentTypes = [.. _defaultAllowedContentTypes];
        }

        if (ExcludedPaths.Count == 0)
        {
            ExcludedPaths = [.. _defaultExcludedPaths];
        }

        if (RedactedPropertyNames.Count == 0)
        {
            RedactedPropertyNames = [.. _defaultRedactedPropertyNames];
        }
    }

    /// <summary>
    /// Validates the list contents that a <see cref="ValidationAttribute"/> cannot express.
    /// </summary>
    /// <returns><see langword="true"/> when every entry is usable.</returns>
    internal bool HasValidEntries() =>
        ExcludedPaths.All(RequestLoggingOptions.IsRootedPath)
        && AllowedContentTypes.All(contentType => !string.IsNullOrWhiteSpace(contentType))
        && RedactedPropertyNames.All(name => !string.IsNullOrWhiteSpace(name));
}
