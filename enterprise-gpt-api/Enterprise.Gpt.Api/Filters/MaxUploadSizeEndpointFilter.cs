using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Problems.Constants;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Enterprise.Gpt.Api.Filters;

/// <summary>Caps an upload endpoint's request body, rejecting anything larger with a 400 problem.</summary>
/// <remarks>
/// Explicit because <c>[RequestSizeLimit]</c> is not guaranteed outside MVC. <c>Content-Length</c> rejects the
/// common case unbuffered, and lowering <see cref="IHttpMaxRequestBodySizeFeature"/> catches chunked uploads.
/// </remarks>
public static class MaxUploadSizeEndpointFilter
{
    #region Private methods

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / (1024 * 1024)} MB"
            : $"{bytes / 1024} KB";
    }

    #endregion

    #region Public static methods

    /// <summary>Builds a filter that rejects request bodies larger than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        return async (context, next) =>
        {
            var httpContext = context.HttpContext;

            if (httpContext.Request.ContentLength > maxBytes)
            {
                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ProblemTypes.UploadTooLarge.Title,
                    Type = ProblemTypes.UploadTooLarge.Type,
                    Detail = $"The file exceeds the maximum upload size of {FormatSize(maxBytes)}.",
                    Extensions = { ["maxBytes"] = maxBytes }
                };

                // See PermissionEndpointFilter: TypedResults.Problem's non-JSON fallback skips
                // CustomizeProblemDetails, so traceId and instance are applied here instead.
                ProblemDetailsRegistration.Enrich(httpContext, problemDetails);

                return TypedResults.Problem(problemDetails);
            }

            var sizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

            // Read-only once the body has started being read, and absent entirely on servers that do not
            // implement it (the in-memory test server, for one) — neither is an error here.
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = maxBytes;
            }

            return await next(context);
        };
    }

    #endregion
}
