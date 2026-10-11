using System.Buffers;
using System.Text;
using Enterprise.Gpt.Api.Options;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Logs every request on the way in and on the way out, through <see cref="ILogger{TCategoryName}"/>.
/// </summary>
/// <remarks>
/// Registered ahead of <c>UseExceptionHandler</c>, so it reports the final status, and therefore ahead of
/// <c>UseAuthentication</c>, so the user is known only on the way out. Not <c>UseHttpLogging</c>: it
/// cannot raise a level for a slow or failed request, and logs under a category pinned to Warning; this
/// category sits outside <c>Microsoft.AspNetCore</c> so the access log is tuned on its own.
/// </remarks>
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

    /// <summary>Runs the pipeline, recording the request either side of it.</summary>
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
                // In the finally: ExceptionHandlerMiddleware rethrows once the response has started, and a
                // mid-stream failure must still leave a record.
                LogCompletion(context, path, _timeProvider.GetElapsedTime(startTimestamp), failure, excluded);
                LogPayload(context, path, requestBody, responseCapture, excluded);
            }
            catch (Exception logFailure)
            {
                // An exception leaving a finally replaces the one propagating, so a broken logger or redactor
                // could turn a working request into a 500 and hide the real fault.
                RequestLogMessages.LogRequestLoggingFailed(_logger, request.Method, path, logFailure);
            }
        }
    }

    private sealed record CapturedBody(string Content, string? ContentType, bool Truncated);

    #region Private methods

    private void LogStart(HttpContext context, string path, bool excluded)
    {
        if (excluded || !_logger.IsEnabled(_options.RequestStartLevel))
        {
            return;
        }

        RequestLogMessages.LogRequestStarted(
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

        // Read straight off the principal rather than through ITokenService, which throws when the
        // request is anonymous — and an anonymous request is one of the cases most worth logging.
        var userId = context.User.GetObjectId();

        RequestLogMessages.LogRequestCompleted(
            _logger,
            level,
            context.Request.Method,
            path,
            statusCode,
            elapsed.TotalMilliseconds,
            (endpoint as RouteEndpoint)?.RoutePattern.RawText,
            endpoint?.DisplayName,
            userId,
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

        RequestLogMessages.LogRequestPayload(
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
            // to a bodiless 499 in GlobalExceptionHandler.
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

        // Keys are kept, values are not: a value is whatever the user typed (search?name=). Keys are still
        // caller-controlled, so they are sanitized like the path.
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

        // Reported rather than read: EnableBuffering spools the whole body, to disk past its threshold, so
        // truncating a large upload would cost all of its I/O to log a few kilobytes.
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
            // Logging must never be the reason a request fails; an unreadable body is a missing log field.
            RequestLogMessages.LogPayloadCaptureFailed(_logger, request.Method, path, exception);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // Outside the try: a read that threw part-way has consumed bytes, and the endpoint must not
            // model-bind a truncated payload. EnableBuffering has run, so the rewind always succeeds.
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
            // The wrapper's pipe writer must reach the connection before the host completes a feature that
            // knows nothing of it. Skipped once the caller has gone: there is nobody left to deliver to.
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
            RequestLogMessages.LogRequestLoggingFailed(_logger, context.Request.Method, path, exception);
        }
        finally
        {
            context.Features.Set(originalFeature);
        }
    }

    // Strips the control characters that would let caller-supplied text forge extra log lines.
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

    #endregion
}
