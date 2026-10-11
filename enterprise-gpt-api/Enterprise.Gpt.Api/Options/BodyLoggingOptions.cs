using Enterprise.Gpt.Api.Middleware;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>Controls whether, and how much of, HTTP payloads are logged.</summary>
/// <remarks>
/// Off by default: bodies carry user prompts and document text. The other settings keep capture from
/// breaking streaming, exhausting memory, or shipping large uploads to telemetry.
/// </remarks>
public sealed class BodyLoggingOptions
{
    private static readonly string[] _defaultAllowedContentTypes =
        ["application/json", "application/problem+json", "text/plain"];

    // All of /api/conversations: the messages route returns the whole transcript and list/search return
    // user titles. The credential route carries a user's third-party token; an uncaptured body cannot leak.
    private static readonly string[] _defaultExcludedPaths =
        ["/api/conversations", "/api/documents", "/api/mcps/*/credential"];

    // downloadUrl is a credential: the signed URL grants read access to anyone holding it until it expires.
    private static readonly string[] _defaultRedactedPropertyNames =
    [
        "password", "token", "accessToken", "refreshToken", "idToken", "secret",
        "apiKey", "clientSecret", "connectionString", "authorization", "downloadUrl",
        // MCP registration headers, redacted wholesale: they travel in api/mcps bodies, and this list is
        // the only guard against a secret an administrator put there by mistake.
        "headers"
    ];

    public bool LogRequestBody { get; set; }

    public bool LogResponseBody { get; set; }

    /// <summary>Level of the payload record, which is written separately from the completion line.</summary>
    /// <remarks>
    /// At <see cref="LogLevel.Debug"/>, capture also needs a category filter that admits Debug.
    /// </remarks>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>Largest number of bytes captured from each body.</summary>
    /// <remarks>
    /// A request declaring a larger <c>Content-Length</c> is skipped, not truncated:
    /// <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/> would spool the whole body.
    /// </remarks>
    public int MaxBodyBytes { get; set; } = 4096;

    /// <summary>
    /// Media types eligible for capture, matched case-insensitively without parameters; any <c>+json</c>
    /// suffix also qualifies.
    /// </summary>
    /// <remarks>
    /// This allow-list keeps binary uploads and downloads out and abandons capture of a
    /// <c>text/event-stream</c> response.
    /// </remarks>
    public IList<string> AllowedContentTypes { get; set; } = [];

    /// <summary>Request paths excluded from capture; a <c>*</c> matches exactly one path segment.</summary>
    /// <remarks>
    /// Paths, not route templates: capture is set up before <c>next()</c>, when no endpoint is resolved yet.
    /// </remarks>
    public IList<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Property names whose values are redacted, matched case-insensitively ignoring <c>_</c> and <c>-</c>.
    /// </summary>
    /// <remarks>
    /// Applied by <see cref="IRequestBodyRedactor"/>; register another implementation before
    /// <c>AddEnterpriseGptRequestLogging</c> to redact by a different rule.
    /// </remarks>
    public IList<string> RedactedPropertyNames { get; set; } = [];

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
