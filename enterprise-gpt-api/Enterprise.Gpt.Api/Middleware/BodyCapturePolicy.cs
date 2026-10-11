namespace Enterprise.Gpt.Api.Middleware;

internal static class BodyCapturePolicy
{
    #region Private methods

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

    #endregion

    #region Public static methods

    /// <remarks>
    /// An allow-list because the failures are asymmetric: a wrong exclusion costs a log line, a wrong inclusion
    /// ships a binary upload or a live <c>text/event-stream</c> into the telemetry backend.
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
    /// <remarks>
    /// Not route templates: capture is arranged before routing resolves an endpoint, so
    /// <c>/api/conversations/*/stream</c> is knowable where <c>{id}</c> is not.
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

    #endregion
}
