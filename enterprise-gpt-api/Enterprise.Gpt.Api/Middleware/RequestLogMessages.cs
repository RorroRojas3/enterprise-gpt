namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// The source-generated log messages <see cref="RequestLoggingMiddleware"/> writes.
/// </summary>
/// <remarks>
/// Generated rather than hand-written so the access log costs an <c>IsEnabled</c> check and nothing else
/// when its level is filtered out — no format-string parsing, no <c>object[]</c>, no boxing of the value
/// types on the completion line.
/// <para>
/// No template carries a trace id. The hosting layer already puts <c>TraceId</c> and <c>SpanId</c> on
/// every log scope, and that is the same 32-hex W3C value <c>ProblemDetailsRegistration.Enrich</c> writes
/// into problem bodies — so a trace id copied out of an error response already finds these records.
/// Repeating it here under the same name with a different value is precisely the defect
/// <c>GlobalExceptionHandler</c> documents.
/// </para>
/// </remarks>
internal static partial class RequestLogMessages
{
    /// <summary>Outcome for a request whose response was written normally, whatever its status.</summary>
    internal const string OutcomeCompleted = "Completed";

    /// <summary>Outcome for a request the caller abandoned before it finished.</summary>
    internal const string OutcomeClientAborted = "ClientAborted";

    /// <summary>Outcome for a request that ended in an exception escaping the pipeline.</summary>
    internal const string OutcomeFaulted = "Faulted";

    /// <summary>
    /// Records a request arriving, before authentication has run.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="level">Level from <see cref="RequestLoggingOptions.RequestStartLevel"/>.</param>
    /// <param name="requestMethod">The HTTP method.</param>
    /// <param name="requestPath">The path, with query values already redacted.</param>
    /// <param name="requestContentType">The declared request content type, if any.</param>
    /// <param name="requestContentLength">The declared request length in bytes, if any.</param>
    /// <param name="userAgent">The caller's user agent, or <see langword="null"/> when not collected.</param>
    /// <param name="clientIp">The caller's IP, or <see langword="null"/> when not collected.</param>
    [LoggerMessage(
        EventId = 1000,
        EventName = "HttpRequestStarted",
        Message = "HTTP {RequestMethod} {RequestPath} started (contentType: {RequestContentType}, " +
                  "contentLength: {RequestContentLength}, userAgent: {UserAgent}, clientIp: {ClientIp})")]
    internal static partial void RequestStarted(
        ILogger logger,
        LogLevel level,
        string requestMethod,
        string requestPath,
        string? requestContentType,
        long? requestContentLength,
        string? userAgent,
        string? clientIp);

    /// <summary>
    /// Records a request finishing, with the status the client actually received.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="level">Severity derived from the status, the outcome and the slow-request threshold.</param>
    /// <param name="requestMethod">The HTTP method.</param>
    /// <param name="requestPath">The path, with query values already redacted.</param>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="elapsedMilliseconds">Wall-clock duration of the whole pipeline.</param>
    /// <param name="routeTemplate">The matched route pattern, or <see langword="null"/> if unrouted.</param>
    /// <param name="endpointName">The matched endpoint's display name, if any.</param>
    /// <param name="userId">The caller's Entra object id, or <see langword="null"/> when anonymous.</param>
    /// <param name="outcome">One of the <c>Outcome*</c> constants on this class.</param>
    /// <param name="responseContentLength">
    /// The response length in bytes, or <see langword="null"/> for a streamed response — the framework
    /// sets no length when it chunks, and the middleware deliberately does not count bytes itself.
    /// </param>
    /// <param name="exception">The escaping exception, or <see langword="null"/> on every other path.</param>
    [LoggerMessage(
        EventId = 1001,
        EventName = "HttpRequestCompleted",
        Message = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMilliseconds} ms " +
                  "(route: {RouteTemplate}, endpoint: {EndpointName}, user: {UserId}, outcome: {Outcome}, " +
                  "responseContentLength: {ResponseContentLength})")]
    internal static partial void RequestCompleted(
        ILogger logger,
        LogLevel level,
        string requestMethod,
        string requestPath,
        int statusCode,
        double elapsedMilliseconds,
        string? routeTemplate,
        string? endpointName,
        string? userId,
        string outcome,
        long? responseContentLength,
        Exception? exception);

    /// <summary>
    /// Records the captured payloads for a request. Written only when body capture is enabled and at
    /// least one body was eligible.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="level">Level from <see cref="BodyLoggingOptions.LogLevel"/>.</param>
    /// <param name="requestMethod">The HTTP method.</param>
    /// <param name="requestPath">The path, with query values already redacted.</param>
    /// <param name="requestContentType">The declared request content type, if any.</param>
    /// <param name="requestBody">The redacted request body, or <see langword="null"/> if not captured.</param>
    /// <param name="responseContentType">The response content type, if any.</param>
    /// <param name="responseBody">The redacted response body, or <see langword="null"/> if not captured.</param>
    /// <param name="truncated">Whether either payload hit the byte cap.</param>
    [LoggerMessage(
        EventId = 1002,
        EventName = "HttpRequestPayload",
        Message = "HTTP {RequestMethod} {RequestPath} payload (requestContentType: {RequestContentType}, " +
                  "requestBody: {RequestBody}, responseContentType: {ResponseContentType}, " +
                  "responseBody: {ResponseBody}, truncated: {Truncated})")]
    internal static partial void RequestPayload(
        ILogger logger,
        LogLevel level,
        string requestMethod,
        string requestPath,
        string? requestContentType,
        string? requestBody,
        string? responseContentType,
        string? responseBody,
        bool truncated);

    /// <summary>
    /// Records that a request body could not be read for logging. The request itself is unaffected.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestMethod">The HTTP method.</param>
    /// <param name="requestPath">The path, with query values already redacted.</param>
    /// <param name="exception">Why the read failed.</param>
    /// <remarks>
    /// Warning, not Debug: a failed read is also the signal that the request body was consumed and had to
    /// be rewound, which is worth seeing in production rather than only under a debug filter.
    /// </remarks>
    [LoggerMessage(
        EventId = 1003,
        EventName = "HttpRequestPayloadCaptureFailed",
        Level = LogLevel.Warning,
        Message = "Could not capture the request body for HTTP {RequestMethod} {RequestPath}")]
    internal static partial void PayloadCaptureFailed(
        ILogger logger,
        string requestMethod,
        string requestPath,
        Exception exception);

    /// <summary>
    /// Records that the access log itself failed. The request is unaffected.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestMethod">The HTTP method.</param>
    /// <param name="requestPath">The path, with query values already redacted.</param>
    /// <param name="exception">What went wrong while logging.</param>
    /// <remarks>
    /// The last-resort arm. If the logger is what is broken this write fails too, which is acceptable —
    /// what is not acceptable is an exception escaping a <c>finally</c> and replacing the fault the
    /// pipeline was already reporting.
    /// </remarks>
    [LoggerMessage(
        EventId = 1004,
        EventName = "HttpRequestLoggingFailed",
        Level = LogLevel.Warning,
        Message = "Request logging failed for HTTP {RequestMethod} {RequestPath}")]
    internal static partial void LoggingFailed(
        ILogger logger,
        string requestMethod,
        string requestPath,
        Exception exception);
}
