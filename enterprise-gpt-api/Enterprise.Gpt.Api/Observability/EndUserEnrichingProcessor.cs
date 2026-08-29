using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using OpenTelemetry;
using System.Diagnostics;

namespace Enterprise.Gpt.Api.Observability;

/// <summary>
/// Stamps the caller's Entra object id onto server spans as <c>enduser.id</c>.
/// </summary>
/// <remarks>
/// Application Insights surfaces the tag as <c>user_AuthenticatedId</c> on the <c>requests</c> row, and
/// there only — an <c>exceptions</c> row comes from a log record, which inherits no span attributes. The
/// access log already carries the same id, so this adds a dimension rather than a data category.
/// </remarks>
/// <param name="httpContextAccessor">Supplies the principal the span was served for.</param>
/// <param name="options">Read once; the switch is a deployment decision, not a per-request one.</param>
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
