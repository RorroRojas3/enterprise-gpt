using Enterprise.Gpt.Api.Middleware;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

/// <summary>Tuning knobs for <see cref="RequestLoggingMiddleware"/>, validated at startup.</summary>
/// <remarks>
/// Lists are filled by <see cref="ApplyListDefaults"/> after binding, not inline: <c>ConfigurationBinder</c>
/// appends into an existing collection, so an inline default would make an allow-list impossible to narrow.
/// </remarks>
public sealed class RequestLoggingOptions
{
    public const string SectionName = "RequestLogging";

    private static readonly string[] _defaultExcludedPaths = ["/health", "/openapi", "/scalar"];

    // Keys that identify no person; the access log needs them to explain list rejections and slow reports.
    private static readonly string[] _defaultSafeQueryKeys =
        ["skip", "take", "page", "pageSize", "sort", "order", "dir", "permissionId", "from", "to", "groupBy"];

    /// <summary>Whether request logging is active.</summary>
    /// <remarks>
    /// Read once at pipeline construction (off leaves the middleware out), so toggling needs a restart.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Level for the line written when a request arrives.</summary>
    /// <remarks>
    /// The completion line is a superset, but only this line covers a still-running SSE stream or a request
    /// whose process died.
    /// </remarks>
    public LogLevel RequestStartLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// How long a successful request may take before its completion line is raised to
    /// <see cref="LogLevel.Warning"/>.
    /// </summary>
    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Path prefixes whose successful requests are not logged, matched case-insensitively by segment.
    /// </summary>
    /// <remarks>Failures are still logged, so a health probe returning 500 is not silenced.</remarks>
    public IList<string> ExcludedPaths { get; set; } = [];

    /// <summary>Whether query string values are masked; keys are always logged.</summary>
    /// <remarks>Values are user content, for example <c>api/conversations/search?name=</c>.</remarks>
    public bool RedactQueryValues { get; set; } = true;

    /// <summary>Query string keys whose values survive redaction, compared case-insensitively.</summary>
    public IList<string> SafeQueryKeys { get; set; } = [];

    public bool IncludeUserAgent { get; set; } = true;

    /// <remarks>
    /// Off by default: an IP address is personal data under the GDPR, and the logged Entra object id
    /// already identifies the caller.
    /// </remarks>
    public bool IncludeClientIp { get; set; }

    public BodyLoggingOptions Bodies { get; set; } = new();

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
    /// Validated per element because the middleware and <see cref="JsonPropertyRedactor"/> build hash sets
    /// from these lists on the first request, where a null entry would be a 500 per request.
    /// </summary>
    internal bool HasValidEntries() =>
        ExcludedPaths.All(IsRootedPath)
        && SafeQueryKeys.All(key => !string.IsNullOrWhiteSpace(key))
        && Bodies.HasValidEntries();

    #region Public static methods

    internal static bool IsRootedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/');

    #endregion
}

internal sealed class RequestLoggingOptionsValidator : AbstractValidator<RequestLoggingOptions>
{
    public RequestLoggingOptionsValidator()
    {
        RuleFor(options => options.SlowRequestThreshold).GreaterThan(TimeSpan.Zero)
            .WithMessage("RequestLogging:SlowRequestThreshold must be greater than zero.");
        RuleFor(options => options.RequestStartLevel).Must(level => Enum.IsDefined(level))
            .WithMessage("RequestLogging:RequestStartLevel must be a valid log level.");
        RuleFor(options => options.ExcludedPaths).Must((options, _) => options.HasValidEntries())
            .WithMessage("RequestLogging list entries must be non-empty, and every path must start with '/'.");
        RuleFor(options => options.Bodies).SetValidator(new BodyLoggingOptionsValidator());
    }
}
