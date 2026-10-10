using Enterprise.Gpt.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Claims;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Observability;

/// <summary>
/// Covers the caller identity stamped onto server spans, and the three cases that must leave a span
/// untouched.
/// </summary>
public sealed class EndUserEnrichingProcessorTests : IDisposable
{
    private const string OidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string EndUserIdTag = "enduser.id";

    private const string SourceName = "Enterprise.Gpt.Unit.Test.EndUserEnrichingProcessorTests";

    private static readonly ActivitySource Source = new(SourceName);

    // Without a listener the source is unsampled and every StartActivity call yields null. Matched by
    // name rather than by reference: AddActivityListener walks the sources that already exist, and a
    // static field read from inside the callback can construct this one mid-walk and never match it.
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        // Both samplers: an ambient parent decides which one the runtime consults.
        SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
    };

    public EndUserEnrichingProcessorTests() => ActivitySource.AddActivityListener(_listener);

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    [Fact]
    public void OnEnd_AuthenticatedServerSpan_CarriesTheCallerObjectId()
    {
        var objectId = Guid.NewGuid();
        using var activity = StartServerActivity();

        Process(activity, Principal(objectId));

        Assert.Equal(objectId.ToString(), activity.GetTagItem(EndUserIdTag));
    }

    [Fact]
    public void OnEnd_AnonymousCaller_LeavesTheSpanUntagged()
    {
        using var activity = StartServerActivity();

        Process(activity, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Null(activity.GetTagItem(EndUserIdTag));
    }

    [Fact]
    public void OnEnd_NoHttpContext_LeavesTheSpanUntagged()
    {
        using var activity = StartServerActivity();

        Process(activity, principal: null);

        Assert.Null(activity.GetTagItem(EndUserIdTag));
    }

    /// <summary>
    /// An outgoing call is served under the same principal, but tagging it would attribute the
    /// dependency to a user rather than to the request that made it.
    /// </summary>
    [Fact]
    public void OnEnd_ClientSpan_LeavesTheSpanUntagged()
    {
        using var activity = StartActivity(ActivityKind.Client);

        Process(activity, Principal(Guid.NewGuid()));

        Assert.Null(activity.GetTagItem(EndUserIdTag));
    }

    [Fact]
    public void OnEnd_CaptureUserIdDisabled_LeavesTheSpanUntagged()
    {
        using var activity = StartServerActivity();

        Process(activity, Principal(Guid.NewGuid()), captureUserId: false);

        Assert.Null(activity.GetTagItem(EndUserIdTag));
    }

    private static void Process(
        Activity activity,
        ClaimsPrincipal? principal,
        bool captureUserId = true)
    {
        var accessor = new HttpContextAccessor();

        if (principal is not null)
        {
            accessor.HttpContext = new DefaultHttpContext { User = principal };
        }

        var processor = new EndUserEnrichingProcessor(
            accessor,
            Options.Create(new TelemetryOptions { CaptureUserId = captureUserId }));

        processor.OnEnd(activity);
    }

    private static ClaimsPrincipal Principal(Guid objectId) =>
        new(new ClaimsIdentity([new Claim(OidClaimType, objectId.ToString())], "Test"));

    private static Activity StartServerActivity() => StartActivity(ActivityKind.Server);

    private static Activity StartActivity(ActivityKind kind)
    {
        var activity = Source.StartActivity("request", kind);
        Assert.NotNull(activity);

        return activity;
    }
}
