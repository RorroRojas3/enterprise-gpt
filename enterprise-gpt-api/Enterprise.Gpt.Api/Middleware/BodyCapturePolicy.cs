namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// The predicates deciding whether a payload may be captured, shared by
/// <see cref="RequestLoggingMiddleware"/> and <see cref="ResponseCaptureStream"/>.
/// </summary>
internal static class BodyCapturePolicy
{
    /// <summary>
    /// Whether a content type is eligible for capture.
    /// </summary>
    /// <param name="contentType">The raw header value, parameters included.</param>
    /// <param name="allowedContentTypes">The configured allow-list.</param>
    /// <returns><see langword="true"/> when the media type is listed or carries a <c>+json</c> suffix.</returns>
    /// <remarks>
    /// An allow-list rather than a deny-list, because the failure modes are asymmetric: a media type
    /// wrongly excluded costs a missing log line, one wrongly included ships a binary upload — or a live
    /// <c>text/event-stream</c> — into the telemetry backend.
    /// </remarks>
    internal static bool IsAllowedContentType(string? contentType, IList<string> allowedContentTypes)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var mediaType = contentType.AsSpan();
        var parameterStart = mediaType.IndexOf(';');
        if (parameterStart >= 0)
        {
            mediaType = mediaType[..parameterStart];
        }

        mediaType = mediaType.Trim();

        if (mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var allowed in allowedContentTypes)
        {
            if (mediaType.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a request path matches any of the configured patterns, where <c>*</c> stands for exactly
    /// one segment and a pattern matches any path it prefixes.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="patterns">The patterns to test.</param>
    /// <returns><see langword="true"/> when at least one pattern matches.</returns>
    /// <remarks>
    /// Deliberately not route templates: capture has to be arranged before the pipeline runs, and routing
    /// has not resolved an endpoint at that point, so <c>api/conversations/{id}/stream</c> is not yet
    /// knowable. <c>/api/conversations/*/stream</c> is.
    /// </remarks>
    internal static bool MatchesAnyPattern(PathString path, IList<string> patterns)
    {
        if (patterns.Count == 0 || !path.HasValue)
        {
            return false;
        }

        var pathSegments = path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pattern in patterns)
        {
            if (Matches(pathSegments, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string[] pathSegments, string pattern)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (patternSegments.Length > pathSegments.Length)
        {
            return false;
        }

        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] is "*")
            {
                continue;
            }

            if (!string.Equals(patternSegments[i], pathSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
