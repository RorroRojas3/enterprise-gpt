using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Problems.Constants;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace Enterprise.Gpt.Api.Filters;

/// <summary>
/// Restricts an endpoint to callers holding <em>every</em> listed permission; others get a 403 problem.
/// </summary>
/// <remarks>
/// A factory because a container-activated filter cannot carry permission ids; it must run after authorization.
/// Grants come from <see cref="IUserPermissionCache"/>, and administrators hold only those recorded for them.
/// </remarks>
public static class PermissionEndpointFilter
{
    #region Private methods

    private static string DescribeRequirement(string[] names)
    {
        return names.Length == 1
            ? $"{names[0]} permission is required."
            : $"{string.Join(", ", names[..^1])} and {names[^1]} permissions are required.";
    }

    #endregion

    #region Public static methods

    /// <summary>
    /// Builds a filter requiring an active grant of every id in <paramref name="permissionIds"/>; duplicates
    /// are ignored.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No permission id was supplied.</exception>
    /// <exception cref="ArgumentException">
    /// A permission id has no name in <see cref="PermissionIds.Names"/>; thrown at map time, failing startup.
    /// </exception>
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
            var grantReader = services.GetRequiredService<IUserGrantReader>();

            // Shared with the services that ask the same question, so they never disagree on what a grant is.
            // The reader resolves its DbContext inside the loader, so a cache hit constructs none.
            var granted = await grantReader.GetGrantsAsync(
                tokenService.GetOid(), context.HttpContext.RequestAborted);

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

                // TypedResults.Problem's non-JSON fallback skips CustomizeProblemDetails, so Enrich
                // (idempotent) runs here to keep a browser's denial correlatable.
                ProblemDetailsRegistration.Enrich(context.HttpContext, problemDetails);

                return TypedResults.Problem(problemDetails);
            }

            return await next(context);
        };
    }

    #endregion
}
