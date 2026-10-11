using System.Diagnostics;
using Enterprise.Gpt.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using OpenTelemetry;

namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// Stamps the caller's Entra object id onto server spans as <c>enduser.id</c>.
/// </summary>
/// <remarks>
/// Surfaces as <c>user_AuthenticatedId</c> on <c>requests</c> rows only: an <c>exceptions</c> row comes
/// from a log record, which inherits no span attributes. The access log already carries the id.
/// </remarks>
internal sealed class EndUserEnrichingProcessor(
    IHttpContextAccessor httpContextAccessor,
    IOptions<TelemetryOptions> options) : BaseProcessor<Activity>
{
    private const string EndUserIdTag = "enduser.id";

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly bool _captureUserId = options.Value.CaptureUserId;

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // OnEnd rather than OnStart: authentication runs inside the server span, so the principal is
        // still anonymous at the point the span begins.
        if (!_captureUserId || data.Kind is not ActivityKind.Server)
        {
            return;
        }

        var objectId = _httpContextAccessor.HttpContext?.User.GetObjectId();

        if (!string.IsNullOrEmpty(objectId))
        {
            data.SetTag(EndUserIdTag, objectId);
        }
    }
}
