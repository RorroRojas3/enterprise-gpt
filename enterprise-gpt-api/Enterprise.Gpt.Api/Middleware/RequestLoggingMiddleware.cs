using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using System.Buffers;
using System.Text;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Logs every request on the way in and on the way out, through <see cref="ILogger{TCategoryName}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The middleware never names a telemetry backend. <see cref="ILogger{TCategoryName}"/> is the seam:
/// Application Insights, Serilog and the console are all just providers registered at the host, and
/// swapping one for another changes nothing here.
/// </para>
/// <para>
/// Its category is <c>Enterprise.Gpt.Api.Middleware.RequestLoggingMiddleware</c>, deliberately outside the
/// <c>Microsoft.AspNetCore</c> prefix that <c>appsettings.json</c> pins to Warning — so the access log is
/// tuned on its own, with <c>"Enterprise.Gpt.Api.Middleware": "Warning"</c> to keep only failures and slow
/// requests.
/// </para>
/// <para>
/// Registered ahead of <c>UseExceptionHandler</c>, which is what lets it report the status the client
/// actually received: the handler writes the response and returns normally, so the status is already final
/// by the time <c>next()</c> comes back. That position also puts it ahead of <c>UseAuthentication</c>, so
/// the caller is anonymous on the way in and the user id can only be reported on the way out.
/// </para>
/// <para>
/// Written rather than configured from <c>UseHttpLogging</c>, which covers the payload half of this well
/// but emits its own records on its own terms: it has no notion of an outcome, cannot raise a level for a
/// slow or failed request, and logs to the <c>Microsoft.AspNetCore.HttpLogging</c> category that this
/// application's configuration already pins to Warning. A single access-log record per request, whose
/// severity carries the meaning, is the thing worth having.
/// </para>
/// </remarks>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="logger">Receives the access log records.</param>
/// <param name="options">Tuning knobs bound from the <c>RequestLogging</c> section.</param>
/// <param name="redactor">Scrubs captured payloads before they are logged.</param>
/// <param name="timeProvider">Supplies the monotonic timestamps the duration is measured with.</param>
public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger,
    IOptions<RequestLoggingOptions> options,
    IRequestBodyRedactor redactor,
    TimeProvider timeProvider)
{
    private const string RedactedQueryValue = "[redacted]";

    private readonly RequestDelegate _next = next;
    private readonly ILogger<RequestLoggingMiddleware> _logger = logger;
    private readonly RequestLoggingOptions _options = options.Value;
    private readonly IRequestBodyRedactor _redactor = redactor;
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly HashSet<string> _safeQueryKeys =
        new(options.Value.SafeQueryKeys, StringComparer.OrdinalIgnoreCase);

    private readonly PathString[] _excludedPaths =
        [.. options.Value.ExcludedPaths.Select(path => new PathString(path))];

    /// <summary>
    /// Runs the pipeline, recording the request either side of it.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task that completes when the rest of the pipeline has.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var excluded = IsExcludedFromSuccessLogging(request.Path);
        var startTimestamp = _timeProvider.GetTimestamp();
        var path = BuildPath(request);

        IHttpResponseBodyFeature? originalBodyFeature = null;
        ResponseCaptureBodyFeature? captureFeature = null;

        // Excluded paths skip capture as well as logging: buffering a body and wrapping a response stream
        // only to discard both at the end is work nobody asked for.
        var requestBody = excluded ? null : await TryCaptureRequestBodyAsync(context, path);
        var responseCapture = excluded
            ? null
            : TryInstallResponseCapture(context, out originalBodyFeature, out captureFeature);

        LogStart(context, path, excluded);

        Exception? failure = null;

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            // Captured for the completion record, not handled: the exception handlers own the response,
            // and swallowing it here would leave the pipeline believing the request succeeded.
            failure = exception;
            throw;
        }
        finally
        {
            await RestoreResponseBodyAsync(context, path, originalBodyFeature, captureFeature);

            try
            {
                // A finally rather than code after the try: ExceptionHandlerMiddleware rethrows once the
                // response has started, and a mid-stream failure is exactly the case that today leaves no
                // trace at all.
                LogCompletion(context, path, _timeProvider.GetElapsedTime(startTimestamp), failure, excluded);
                LogPayload(context, path, requestBody, responseCapture, excluded);
            }
            catch (Exception logFailure)
            {
                // An exception leaving a finally replaces whatever was already propagating, so an
                // unlucky logger or a custom redactor could turn a working request into a 500 and hide
                // the real fault on the way. Nothing the access log does is worth that.
                RequestLogMessages.LoggingFailed(_logger, request.Method, path, logFailure);
            }
        }
    }

    private void LogStart(HttpContext context, string path, bool excluded)
    {
        if (excluded || !_logger.IsEnabled(_options.RequestStartLevel))
        {
            return;
        }

        RequestLogMessages.RequestStarted(
            _logger,
            _options.RequestStartLevel,
            context.Request.Method,
            path,
            context.Request.ContentType,
            context.Request.ContentLength,
            _options.IncludeUserAgent ? NullIfEmpty(Sanitize(context.Request.Headers.UserAgent.ToString())) : null,
            _options.IncludeClientIp ? context.Connection.RemoteIpAddress?.ToString() : null);
    }

    private void LogCompletion(HttpContext context, string path, TimeSpan elapsed, Exception? failure, bool excluded)
    {
        var statusCode = context.Response.StatusCode;
        var outcome = SelectOutcome(context, failure);

        // An excluded path stays silent while it is healthy and speaks up when it is not: silencing a
        // probe is worthwhile, silencing a probe that has started failing is not.
        if (excluded && failure is null && statusCode < StatusCodes.Status400BadRequest)
        {
            return;
        }

        var level = SelectLevel(statusCode, elapsed, outcome);

        if (!_logger.IsEnabled(level))
        {
            return;
        }

        var endpoint = context.GetEndpoint();

        RequestLogMessages.RequestCompleted(
            _logger,
            level,
            context.Request.Method,
            path,
            statusCode,
            elapsed.TotalMilliseconds,
            (endpoint as RouteEndpoint)?.RoutePattern.RawText,
            endpoint?.DisplayName,
            // Read straight off the principal rather than through ITokenService, which throws when the
            // request is anonymous — and an anonymous request is one of the cases most worth logging.
            context.User.GetObjectId(),
            outcome,
            context.Response.ContentLength,
            failure);
    }

    private void LogPayload(
        HttpContext context,
        string path,
        CapturedBody? requestBody,
        ResponseCaptureStream? responseCapture,
        bool excluded)
    {
        var level = _options.Bodies.LogLevel;

        if (excluded || (requestBody is null && responseCapture is null) || !_logger.IsEnabled(level))
        {
            return;
        }

        // Decoded only once both guards have passed — materializing the captured bytes is the expensive
        // part of this method.
        var responseText = responseCapture?.GetCapturedText();

        if (requestBody is null && responseText is null)
        {
            return;
        }

        var responseContentType = responseCapture?.CapturedContentType;

        RequestLogMessages.RequestPayload(
            _logger,
            level,
            context.Request.Method,
            path,
            requestBody?.ContentType,
            requestBody is null ? null : Sanitize(_redactor.Redact(requestBody.Content, requestBody.ContentType)),
            responseContentType,
            responseText is null ? null : Sanitize(_redactor.Redact(responseText, responseContentType)),
            (requestBody?.Truncated ?? false) || (responseCapture?.Truncated ?? false));
    }

    private static string SelectOutcome(HttpContext context, Exception? failure) =>
        failure is not null
            ? RequestLogMessages.OutcomeFaulted
            : context.RequestAborted.IsCancellationRequested
                ? RequestLogMessages.OutcomeClientAborted
                : RequestLogMessages.OutcomeCompleted;

    private LogLevel SelectLevel(int statusCode, TimeSpan elapsed, string outcome) =>
        outcome switch
        {
            RequestLogMessages.OutcomeFaulted => LogLevel.Error,
            // Not a warning: a user navigating away mid-stream is ordinary traffic here, and already maps
            // to a bodiless 499 in OperationCanceledExceptionHandler.
            RequestLogMessages.OutcomeClientAborted => LogLevel.Information,
            _ when statusCode >= StatusCodes.Status500InternalServerError => LogLevel.Error,
            _ when statusCode >= StatusCodes.Status400BadRequest => LogLevel.Warning,
            _ when elapsed > _options.SlowRequestThreshold => LogLevel.Warning,
            _ => LogLevel.Information
        };

    private bool IsExcludedFromSuccessLogging(PathString path)
    {
        foreach (var excluded in _excludedPaths)
        {
            if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string BuildPath(HttpRequest request)
    {
        // Sanitized because Path.Value is percent-decoded: a request for /api/x%0A... would otherwise put
        // a real newline into the record and forge a second line in any text-based sink (CWE-117).
        var path = Sanitize(request.Path.HasValue ? request.Path.Value! : "/");

        if (!request.QueryString.HasValue)
        {
            return path;
        }

        if (!_options.RedactQueryValues)
        {
            return string.Concat(path, Sanitize(request.QueryString.Value ?? string.Empty));
        }

        // Keys are kept, values are not: a key names a parameter, a value here is whatever the user
        // typed — api/conversations/search?name= is a search term. Keys are still caller-controlled, so
        // they get the same control-character treatment as the path.
        var builder = new StringBuilder(path);
        var separator = '?';

        foreach (var (key, values) in request.Query)
        {
            builder.Append(separator).Append(Sanitize(key)).Append('=');
            builder.Append(_safeQueryKeys.Contains(key) ? Sanitize(values.ToString()) : RedactedQueryValue);
            separator = '&';
        }

        return builder.ToString();
    }

    private async Task<CapturedBody?> TryCaptureRequestBodyAsync(HttpContext context, string path)
    {
        var bodies = _options.Bodies;
        var request = context.Request;

        if (!bodies.LogRequestBody
            || BodyCapturePolicy.MatchesAnyPattern(request.Path, bodies.ExcludedPaths)
            || !BodyCapturePolicy.IsAllowedContentType(request.ContentType, bodies.AllowedContentTypes))
        {
            return null;
        }

        // A chunked body declares no length, so its size is unknown until it has been read — which is the
        // same problem as an oversized one.
        if (request.ContentLength is not { } contentLength || contentLength <= 0)
        {
            return null;
        }

        // Oversized bodies are reported rather than read. EnableBuffering spools the entire body — to
        // disk past its own threshold — however few bytes are ultimately wanted, so truncating a large
        // request would cost the whole upload's worth of I/O to log four kilobytes of it.
        if (contentLength > bodies.MaxBodyBytes)
        {
            return new CapturedBody(
                $"[skipped: {contentLength} bytes exceeds RequestLogging:Bodies:MaxBodyBytes of {bodies.MaxBodyBytes}]",
                request.ContentType,
                Truncated: true);
        }

        request.EnableBuffering(bufferThreshold: bodies.MaxBodyBytes);

        // Pooled rather than allocated: the validated cap runs to 1 MB, and a plain array above 85 KB is
        // a large object heap allocation on every request carrying a body.
        var buffer = ArrayPool<byte>.Shared.Rent((int)contentLength);

        try
        {
            var read = await request.Body.ReadAtLeastAsync(
                buffer.AsMemory(0, (int)contentLength),
                (int)contentLength,
                throwOnEndOfStream: false,
                context.RequestAborted);

            return read == 0 ? null : new CapturedBody(Encoding.UTF8.GetString(buffer, 0, read), request.ContentType, Truncated: false);
        }
        catch (Exception exception)
        {
            // Logging must never be the reason a request fails. A body that could not be read is a
            // missing log field; letting the exception out would turn it into a failed request.
            RequestLogMessages.PayloadCaptureFailed(_logger, request.Method, path, exception);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // Outside the try: a read that threw part-way has already consumed bytes, and leaving the
            // stream mid-body would hand the endpoint a truncated payload to model-bind. EnableBuffering
            // has run by here, so the rewind always succeeds.
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private ResponseCaptureStream? TryInstallResponseCapture(
        HttpContext context,
        out IHttpResponseBodyFeature? originalFeature,
        out ResponseCaptureBodyFeature? captureFeature)
    {
        originalFeature = null;
        captureFeature = null;
        var bodies = _options.Bodies;

        if (!bodies.LogResponseBody || BodyCapturePolicy.MatchesAnyPattern(context.Request.Path, bodies.ExcludedPaths))
        {
            return null;
        }

        var feature = context.Features.Get<IHttpResponseBodyFeature>();

        if (feature is null)
        {
            return null;
        }

        originalFeature = feature;
        var capture = new ResponseCaptureStream(feature.Stream, context.Response, bodies);
        captureFeature = new ResponseCaptureBodyFeature(feature, capture);
        context.Features.Set<IHttpResponseBodyFeature>(captureFeature);

        return capture;
    }

    private async ValueTask RestoreResponseBodyAsync(
        HttpContext context,
        string path,
        IHttpResponseBodyFeature? originalFeature,
        ResponseCaptureBodyFeature? captureFeature)
    {
        if (originalFeature is null)
        {
            return;
        }

        try
        {
            // Anything a handler left in the wrapper's pipe writer has to reach the connection before the
            // host goes on to complete a feature that knows nothing about that writer. Skipped once the
            // caller has gone: there is nobody left to deliver to, and a client navigating away
            // mid-stream is ordinary traffic here rather than something to warn about.
            if (captureFeature is not null && !context.RequestAborted.IsCancellationRequested)
            {
                await captureFeature.FlushWriterAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller disconnected between the check above and the flush.
        }
        catch (Exception exception)
        {
            // Throwing from here would replace whatever fault the pipeline was already reporting.
            RequestLogMessages.LoggingFailed(_logger, context.Request.Method, path, exception);
        }
        finally
        {
            context.Features.Set(originalFeature);
        }
    }

    /// <summary>
    /// Strips the control characters that would let caller-supplied text forge extra log lines.
    /// </summary>
    private static string Sanitize(string value)
    {
        var needsWork = false;

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return value;
        }

        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed record CapturedBody(string Content, string? ContentType, bool Truncated);
}
