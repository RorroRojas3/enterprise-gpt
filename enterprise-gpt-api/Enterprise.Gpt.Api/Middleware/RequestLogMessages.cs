using Enterprise.Gpt.Api.Options;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Source-generated so a filtered-out access log costs an <c>IsEnabled</c> check and nothing else.
/// </summary>
/// <remarks>
/// No template carries a trace id: every log scope already has <c>TraceId</c>, the same value
/// <c>ProblemDetailsRegistration.Enrich</c> writes into problem bodies.
/// </remarks>
internal static partial class RequestLogMessages
{
    /// <summary>Outcome for a request whose response was written normally, whatever its status.</summary>
    internal const string OutcomeCompleted = "Completed";

    /// <summary>Outcome for a request the caller abandoned before it finished.</summary>
    internal const string OutcomeClientAborted = "ClientAborted";

    /// <summary>Outcome for a request that ended in an exception escaping the pipeline.</summary>
    internal const string OutcomeFaulted = "Faulted";

    #region Logging

    /// <summary>
    /// Records a request arriving, before authentication, at
    /// <see cref="RequestLoggingOptions.RequestStartLevel"/>.
    /// </summary>
    [LoggerMessage(
        EventId = 1000,
        EventName = "HttpRequestStarted",
        Message = "HTTP {RequestMethod} {RequestPath} started (contentType: {RequestContentType}, " +
                  "contentLength: {RequestContentLength}, userAgent: {UserAgent}, clientIp: {ClientIp})")]
    internal static partial void LogRequestStarted(
        ILogger logger,
        LogLevel level,
        string requestMethod,
        string requestPath,
        string? requestContentType,
        long? requestContentLength,
        string? userAgent,
        string? clientIp);

    /// <summary>Records a request finishing, with the status the client actually received.</summary>
    /// <remarks>
    /// <paramref name="responseContentLength"/> is <see langword="null"/> for a streamed response: the
    /// framework sets no length when it chunks, and the middleware deliberately does not count bytes.
    /// </remarks>
    [LoggerMessage(
        EventId = 1001,
        EventName = "HttpRequestCompleted",
        Message = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMilliseconds} ms " +
                  "(route: {RouteTemplate}, endpoint: {EndpointName}, user: {UserId}, outcome: {Outcome}, " +
                  "responseContentLength: {ResponseContentLength})")]
    internal static partial void LogRequestCompleted(
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
    /// Records the captured payloads at <see cref="BodyLoggingOptions.LogLevel"/>, only when capture is enabled
    /// and at least one body was eligible.
    /// </summary>
    [LoggerMessage(
        EventId = 1002,
        EventName = "HttpRequestPayload",
        Message = "HTTP {RequestMethod} {RequestPath} payload (requestContentType: {RequestContentType}, " +
                  "requestBody: {RequestBody}, responseContentType: {ResponseContentType}, " +
                  "responseBody: {ResponseBody}, truncated: {Truncated})")]
    internal static partial void LogRequestPayload(
        ILogger logger,
        LogLevel level,
        string requestMethod,
        string requestPath,
        string? requestContentType,
        string? requestBody,
        string? responseContentType,
        string? responseBody,
        bool truncated);

    /// <summary>Records that a request body could not be read for logging; the request is unaffected.</summary>
    /// <remarks>
    /// Warning, not Debug: a failed read also signals that the body was consumed and had to be rewound.
    /// </remarks>
    [LoggerMessage(
        EventId = 1003,
        EventName = "HttpRequestPayloadCaptureFailed",
        Level = LogLevel.Warning,
        Message = "Could not capture the request body for HTTP {RequestMethod} {RequestPath}")]
    internal static partial void LogPayloadCaptureFailed(
        ILogger logger,
        string requestMethod,
        string requestPath,
        Exception exception);

    /// <summary>Records that the access log itself failed; the request is unaffected.</summary>
    /// <remarks>
    /// The last-resort arm: if the logger is broken this fails too, but no exception may escape a
    /// <c>finally</c> and replace the fault the pipeline was already reporting.
    /// </remarks>
    [LoggerMessage(
        EventId = 1004,
        EventName = "HttpRequestLoggingFailed",
        Level = LogLevel.Warning,
        Message = "Request logging failed for HTTP {RequestMethod} {RequestPath}")]
    internal static partial void LogRequestLoggingFailed(
        ILogger logger,
        string requestMethod,
        string requestPath,
        Exception exception);

    #endregion
}
