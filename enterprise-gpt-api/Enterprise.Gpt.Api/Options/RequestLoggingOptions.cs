using Enterprise.Gpt.Api.Middleware;
using FluentValidation;

namespace Enterprise.Gpt.Api.Options;

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
    // to explain the two rejections the list routes can now raise. The three reporting keys join
    // them on the same test — two timestamps and a dimension name — and the range is the one thing
    // that explains why a report was slow.
    private static readonly string[] _defaultSafeQueryKeys =
        ["skip", "take", "page", "pageSize", "sort", "order", "dir", "permissionId", "from", "to", "groupBy"];

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
    /// Validates the list contents, which no single-property rule can express.
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
