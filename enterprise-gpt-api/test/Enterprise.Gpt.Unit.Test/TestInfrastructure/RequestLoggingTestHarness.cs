using System.Security.Claims;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Builds a <see cref="RequestLoggingMiddleware"/> over a collecting logger and a controllable clock, and
/// reads the records back out.
/// </summary>
public sealed class RequestLoggingTestHarness
{
    /// <summary>The Entra claim type the API keys users on.</summary>
    public const string OidClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public const int StartedEventId = 1000;

    public const int CompletedEventId = 1001;

    public const int PayloadEventId = 1002;

    public const int CaptureFailedEventId = 1003;

    public const int LoggingFailedEventId = 1004;

    /// <summary>Already carries the production list defaults; mutate before <see cref="Run"/>.</summary>
    public RequestLoggingOptions Options { get; } = CreateDefaultedOptions();

    public IRequestBodyRedactor? Redactor { get; set; }

    public FakeLogCollector Collector { get; } = new();

    /// <summary>The clock duration is measured against. Advance it inside the pipeline to fake a slow request.</summary>
    public MutableTimeProvider Time { get; } = new();

    public Task Run(HttpContext context, RequestDelegate? next = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);

        var middleware = new RequestLoggingMiddleware(
            next ?? (_ => Task.CompletedTask),
            new FakeLogger<RequestLoggingMiddleware>(Collector),
            options,
            Redactor ?? new JsonPropertyRedactor(options),
            Time);

        return middleware.InvokeAsync(context);
    }

    /// <summary>
    /// Runs the same post-bind list defaults as startup; a fresh instance has empty allow-lists.
    /// </summary>
    public static RequestLoggingOptions CreateDefaultedOptions()
    {
        var options = new RequestLoggingOptions();
        options.ApplyListDefaults();

        return options;
    }

    public FakeLogRecord SingleRecord(int eventId) =>
        Assert.Single(Collector.GetSnapshot(), record => record.Id.Id == eventId);

    public IReadOnlyList<FakeLogRecord> All(int eventId) =>
        [.. Collector.GetSnapshot().Where(record => record.Id.Id == eventId)];

    public static string? Field(FakeLogRecord record, string name) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;

    /// <summary>
    /// <paramref name="queryString"/> includes the leading <c>?</c>; a null <paramref name="userId"/>
    /// stays anonymous.
    /// </summary>
    public static DefaultHttpContext CreateContext(
        string path = "/api/models",
        string method = "GET",
        string? queryString = null,
        Guid? userId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        if (userId is { } id)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(OidClaimType, id.ToString())], "Test"));
        }

        return context;
    }

    public static RecordingStream CaptureResponseBody(DefaultHttpContext context)
    {
        var stream = new RecordingStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(stream));

        return stream;
    }
}
