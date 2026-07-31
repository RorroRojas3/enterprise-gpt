using Microsoft.EntityFrameworkCore;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using System.Net;

namespace Enterprise.Gpt.Api.Filters
{
    /// <summary>
    /// Endpoint filter that restricts an endpoint to callers holding an active
    /// <see cref="PermissionIds.Administrator"/> grant, returning a 403 with the standard
    /// <see cref="ErrorDto"/> shape otherwise. Instantiated per request from
    /// <c>HttpContext.RequestServices</c> by <c>AddEndpointFilter&lt;T&gt;()</c> —
    /// do not register in DI. Must run after authorization so a principal exists.
    /// </summary>
    public class AdminEndpointFilter(ITokenService tokenService, EnterpriseGptDbContext ctx) : IEndpointFilter
    {
        private readonly ITokenService _tokenService = tokenService;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        /// <inheritdoc />
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var oid = _tokenService.GetOid();
            var cancellationToken = context.HttpContext.RequestAborted;

            var isAdmin = await _ctx.UserPermissions
                .AsNoTracking()
                .AnyAsync(p => p.UserId == oid
                    && p.PermissionId == PermissionIds.Administrator
                    && !p.DateDeactivated.HasValue, cancellationToken);

            if (!isAdmin)
            {
                return TypedResults.Json(new ErrorDto
                {
                    StatusCode = HttpStatusCode.Forbidden,
                    Errors = ["Administrator permission is required."],
                    TraceId = context.HttpContext.TraceIdentifier,
                    Timestamp = DateTimeOffset.UtcNow
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        }
    }
}
