using Microsoft.AspNetCore.Http.Features;
using Enterprise.Gpt.Dto;
using System.Net;

namespace Enterprise.Gpt.Api.Filters
{
    /// <summary>
    /// Endpoint filter factory that caps the request body size for an upload endpoint, rejecting anything
    /// larger with a 400 in the standard <see cref="ErrorDto"/> shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server-wide Kestrel limit is far larger than any document this API accepts, and
    /// <c>[RequestSizeLimit]</c> is an MVC filter whose enforcement outside MVC is not guaranteed — so the
    /// limit is applied explicitly here instead.
    /// </para>
    /// <para>
    /// Two layers, because either alone is insufficient. The <c>Content-Length</c> check rejects the common
    /// case before a single byte is buffered, and with a message that says what the limit is. Lowering
    /// <see cref="IHttpMaxRequestBodySizeFeature"/> then covers a chunked upload that declares no length, so
    /// the server aborts the connection once the body actually exceeds the cap rather than buffering it all.
    /// </para>
    /// </remarks>
    public static class MaxUploadSizeEndpointFilter
    {
        /// <summary>
        /// Builds an endpoint filter that rejects request bodies larger than <paramref name="maxBytes"/>.
        /// </summary>
        /// <param name="maxBytes">The maximum accepted body size, in bytes.</param>
        /// <returns>A filter delegate suitable for <c>AddEndpointFilter</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxBytes"/> is not positive.</exception>
        public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(long maxBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

            return async (context, next) =>
            {
                var httpContext = context.HttpContext;

                if (httpContext.Request.ContentLength > maxBytes)
                {
                    return TypedResults.Json(new ErrorDto
                    {
                        StatusCode = HttpStatusCode.BadRequest,
                        Errors = [$"The file exceeds the maximum upload size of {FormatSize(maxBytes)}."],
                        TraceId = httpContext.TraceIdentifier,
                        Timestamp = DateTimeOffset.UtcNow
                    }, statusCode: StatusCodes.Status400BadRequest);
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

        private static string FormatSize(long bytes)
        {
            return bytes >= 1024 * 1024
                ? $"{bytes / (1024 * 1024)} MB"
                : $"{bytes / 1024} KB";
        }
    }
}
