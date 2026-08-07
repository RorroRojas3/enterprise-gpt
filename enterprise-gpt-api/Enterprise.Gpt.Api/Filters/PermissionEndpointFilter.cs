using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Caching;

namespace Enterprise.Gpt.Api.Filters
{
    /// <summary>
    /// Endpoint filter factory that restricts an endpoint to callers holding <em>every</em> listed
    /// permission, returning a 403 <see cref="ProblemDetails"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generic form of <c>AddEndpointFilter</c> activates a type from the container and so cannot
    /// carry permission ids, which is why this is a factory returning a delegate rather than an
    /// <see cref="IEndpointFilter"/> implementation. Services are resolved per request from
    /// <c>HttpContext.RequestServices</c>. Must run after authorization so a principal exists.
    /// </para>
    /// <para>
    /// Authorization reads <see cref="IUserPermissionCache"/>, not the database: the entry is normally
    /// written at sign-in, and on a miss this filter loads the caller's full grant set once and caches
    /// it. Administrators are not implicitly granted every permission — they hold the grants the
    /// permission service recorded, exactly.
    /// </para>
    /// </remarks>
    public static class PermissionEndpointFilter
    {
        /// <summary>
        /// Builds an endpoint filter requiring an active grant of every id in
        /// <paramref name="permissionIds"/>.
        /// </summary>
        /// <param name="permissionIds">
        /// The permissions the caller must all hold, from <see cref="PermissionIds"/>. Duplicates are
        /// ignored.
        /// </param>
        /// <returns>A filter delegate suitable for <c>AddEndpointFilter</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">No permission id was supplied.</exception>
        /// <exception cref="ArgumentException">
        /// A permission id has no display name in <see cref="PermissionIds.Names"/>. Thrown at map
        /// time, so the app fails to start rather than emitting a nameless 403.
        /// </exception>
        /// <example>
        /// <code language="csharp">
        /// group.MapPost("", UploadAsync)
        ///     .AddEndpointFilter(PermissionEndpointFilter.Require(PermissionIds.UploadFile))
        ///     .ProducesProblem(StatusCodes.Status403Forbidden);
        /// </code>
        /// </example>
        public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(
            params Guid[] permissionIds)
        {
            ArgumentNullException.ThrowIfNull(permissionIds);
            ArgumentOutOfRangeException.ThrowIfZero(permissionIds.Length, nameof(permissionIds));

            Guid[] required = [.. permissionIds.Distinct()];
            foreach (var permissionId in required)
            {
                if (!PermissionIds.Names.ContainsKey(permissionId))
                {
                    throw new ArgumentException(
                        $"Permission {permissionId} has no entry in {nameof(PermissionIds)}.{nameof(PermissionIds.Names)}.",
                        nameof(permissionIds));
                }
            }

            // Formatted once at map time; the request path only copies the string and the array.
            string[] names = [.. required.Select(id => PermissionIds.Names[id])];
            var message = DescribeRequirement(names);

            return async (context, next) =>
            {
                var services = context.HttpContext.RequestServices;
                var tokenService = services.GetRequiredService<ITokenService>();
                var permissionCache = services.GetRequiredService<IUserPermissionCache>();

                var oid = tokenService.GetOid();
                var cancellationToken = context.HttpContext.RequestAborted;

                // The DbContext is resolved inside the loader, so a cache hit — the normal case,
                // since signing in populates the entry — constructs no EF context here at all.
                var granted = await permissionCache.GetOrLoadAsync(
                    oid,
                    token => LoadGrantsAsync(services, oid, token),
                    cancellationToken);

                foreach (var permissionId in required)
                {
                    if (granted.Contains(permissionId))
                    {
                        continue;
                    }

                    // The message names the whole requirement rather than the missing part, so the
                    // response does not vary with which grants the caller happens to hold.
                    var problemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = ProblemTypes.PermissionRequired.Title,
                        Type = ProblemTypes.PermissionRequired.Type,
                        Detail = message,
                        Extensions = { ["permissions"] = names }
                    };

                    // TypedResults.Problem falls back to a direct write when the client's Accept
                    // excludes JSON, and that fallback skips CustomizeProblemDetails — so a denial
                    // would arrive uncorrelatable for exactly the browser case. Enrich is idempotent.
                    ProblemDetailsRegistration.Enrich(context.HttpContext, problemDetails);

                    return TypedResults.Problem(problemDetails);
                }

                return await next(context);
            };
        }

        /// <summary>
        /// Reads the caller's full active grant set on a cache miss. Deliberately unfiltered by
        /// permission id: one query serves every filter the request passes through, and the entry is
        /// reused for the caller's whole session.
        /// </summary>
        private static async Task<IReadOnlyCollection<Guid>> LoadGrantsAsync(
            IServiceProvider services,
            Guid oid,
            CancellationToken cancellationToken)
        {
            var ctx = services.GetRequiredService<EnterpriseGptDbContext>();

            // User.Id is the Entra object id, so the grant table is queried directly with no join.
            return await ctx.UserPermissions
                .AsNoTracking()
                .Where(up => up.UserId == oid && !up.DateDeactivated.HasValue)
                .Select(up => up.PermissionId)
                .ToListAsync(cancellationToken);
        }

        private static string DescribeRequirement(string[] names)
        {
            return names.Length == 1
                ? $"{names[0]} permission is required."
                : $"{string.Join(", ", names[..^1])} and {names[^1]} permissions are required.";
        }
    }
}
