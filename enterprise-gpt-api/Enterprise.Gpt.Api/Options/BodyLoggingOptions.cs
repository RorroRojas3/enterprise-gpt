using Enterprise.Gpt.Api.Middleware;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

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
    // routes return user-authored titles. The MCP credential route carries a user's own third-party
    // token; "apiKey" is on the redaction list below, but a body never captured cannot leak through a
    // payload the parser fails on either.
    private static readonly string[] _defaultExcludedPaths =
        ["/api/conversations", "/api/documents", "/api/mcps/*/credential"];

    // downloadUrl is on the list because it is a credential, not a link: the signed document download
    // URL grants read access to anyone holding it until it expires. /api/documents is excluded from
    // capture already, so this only matters once an operator narrows ExcludedPaths.
    private static readonly string[] _defaultRedactedPropertyNames =
    [
        "password", "token", "accessToken", "refreshToken", "idToken", "secret",
        "apiKey", "clientSecret", "connectionString", "authorization", "downloadUrl",
        // The MCP registration's configured request headers, redacted wholesale as an object.
        // They are validated as non-secret configuration and never logged by `McpToolProvider`,
        // but they travel in the `api/mcps` request and response bodies — and this list is the
        // only thing standing between an administrator's typo and a log line carrying it.
        "headers"
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
    /// Defaults to <c>/api/conversations</c>, <c>/api/documents</c> and the MCP credential route
    /// when left unset.
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
    /// container before <c>AddEnterpriseGptRequestLogging</c> runs to redact by some other rule.
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
    /// Validates the list contents, which no single-property rule can express.
    /// </summary>
    /// <returns><see langword="true"/> when every entry is usable.</returns>
    internal bool HasValidEntries() =>
        ExcludedPaths.All(RequestLoggingOptions.IsRootedPath)
        && AllowedContentTypes.All(contentType => !string.IsNullOrWhiteSpace(contentType))
        && RedactedPropertyNames.All(name => !string.IsNullOrWhiteSpace(name));
}

internal sealed class BodyLoggingOptionsValidator : AbstractValidator<BodyLoggingOptions>
{
    public BodyLoggingOptionsValidator()
    {
        RuleFor(options => options.LogLevel).Must(level => Enum.IsDefined(level))
            .WithMessage("RequestLogging:Bodies:LogLevel must be a valid log level.");
        RuleFor(options => options.MaxBodyBytes).InclusiveBetween(1, 1024 * 1024)
            .WithMessage("RequestLogging:Bodies:MaxBodyBytes must be between 1 and 1048576.");
    }
}
