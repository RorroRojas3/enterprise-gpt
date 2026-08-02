using Microsoft.EntityFrameworkCore;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using System.Net;

namespace Enterprise.Gpt.Api.Filters
{
    /// <summary>
    /// Endpoint filter factory that restricts an endpoint to callers holding a specific active permission,
    /// returning a 403 with the standard <see cref="ErrorDto"/> shape otherwise.
    /// </summary>
    /// <remarks>
    /// The generic form of <c>AddEndpointFilter</c> activates a type from the container and so cannot carry
    /// a permission id, which is why this is a factory returning a delegate rather than an
    /// <see cref="IEndpointFilter"/> implementation like <see cref="AdminEndpointFilter"/>. Services are
    /// resolved per request from <c>HttpContext.RequestServices</c>. Must run after authorization so a
    /// principal exists.
    /// </remarks>
    public static class PermissionEndpointFilter
    {
        /// <summary>
        /// Builds an endpoint filter requiring an active grant of <paramref name="permissionId"/>.
        /// </summary>
        /// <param name="permissionId">The permission the caller must hold, from <see cref="PermissionIds"/>.</param>
        /// <param name="permissionName">The permission's display name, used in the 403 message.</param>
        /// <returns>A filter delegate suitable for <c>AddEndpointFilter</c>.</returns>
        /// <example>
        /// <code language="csharp">
        /// group.MapPost("", UploadAsync)
        ///     .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile, "Upload File"))
        ///     .Produces&lt;ErrorDto&gt;(StatusCodes.Status403Forbidden);
        /// </code>
        /// </example>
        public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(Guid permissionId, string permissionName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(permissionName);

            return async (context, next) =>
            {
                var services = context.HttpContext.RequestServices;
                var tokenService = services.GetRequiredService<ITokenService>();
                var ctx = services.GetRequiredService<EnterpriseGptDbContext>();

                var oid = tokenService.GetOid();
                var cancellationToken = context.HttpContext.RequestAborted;

                // Administrators are not implicitly granted every permission: they hold the grants they
                // hold, exactly as the permission service records them.
                var hasPermission = await ctx.UserPermissions
                    .AsNoTracking()
                    .AnyAsync(p => p.UserId == oid
                        && p.PermissionId == permissionId
                        && !p.DateDeactivated.HasValue, cancellationToken);

                if (!hasPermission)
                {
                    return TypedResults.Json(new ErrorDto
                    {
                        StatusCode = HttpStatusCode.Forbidden,
                        Errors = [$"{permissionName} permission is required."],
                        TraceId = context.HttpContext.TraceIdentifier,
                        Timestamp = DateTimeOffset.UtcNow
                    }, statusCode: StatusCodes.Status403Forbidden);
                }

                return await next(context);
            };
        }
    }
}
