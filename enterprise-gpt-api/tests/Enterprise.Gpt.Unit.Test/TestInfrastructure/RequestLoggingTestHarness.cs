using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Api.Middleware;
using System.Security.Claims;
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

    /// <summary>Event id of the record written when a request arrives.</summary>
    public const int StartedEventId = 1000;

    /// <summary>Event id of the record written when a request finishes.</summary>
    public const int CompletedEventId = 1001;

    /// <summary>Event id of the record carrying captured payloads.</summary>
    public const int PayloadEventId = 1002;

    /// <summary>Event id of the record written when a request body could not be read.</summary>
    public const int CaptureFailedEventId = 1003;

    /// <summary>Event id of the record written when the access log itself failed.</summary>
    public const int LoggingFailedEventId = 1004;

    /// <summary>
    /// Settings the middleware is built with, already carrying the production defaults. Mutate before
    /// calling <see cref="Run"/>.
    /// </summary>
    public RequestLoggingOptions Options { get; } = CreateDefaultedOptions();

    /// <summary>Replaces the default redactor. Set before calling <see cref="Run"/>.</summary>
    public IRequestBodyRedactor? Redactor { get; set; }

    /// <summary>Receives every record the middleware writes.</summary>
    public FakeLogCollector Collector { get; } = new();

    /// <summary>The clock duration is measured against. Advance it inside the pipeline to fake a slow request.</summary>
    public MutableTimeProvider Time { get; } = new();

    /// <summary>
    /// Runs the middleware over <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The request to process.</param>
    /// <param name="next">The rest of the pipeline. Defaults to a no-op.</param>
    /// <returns>A task that completes when the middleware has.</returns>
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
    /// Builds options carrying the same list defaults the registration applies at startup.
    /// </summary>
    /// <returns>The defaulted options.</returns>
    /// <remarks>
    /// The lists are empty on a freshly constructed instance — the configuration binder appends to a
    /// pre-populated collection rather than replacing it, so the defaults are applied post-bind instead
    /// of inline. Tests have to go through the same step or they would run against empty allow-lists.
    /// </remarks>
    public static RequestLoggingOptions CreateDefaultedOptions()
    {
        var options = new RequestLoggingOptions();
        options.ApplyListDefaults();

        return options;
    }

    /// <summary>
    /// Returns the single record with the given event id, or fails when there is not exactly one.
    /// </summary>
    /// <param name="eventId">One of the <c>*EventId</c> constants on this class.</param>
    /// <returns>The matching record.</returns>
    public FakeLogRecord Single(int eventId) =>
        Assert.Single(Collector.GetSnapshot(), record => record.Id.Id == eventId);

    /// <summary>
    /// Returns every record with the given event id.
    /// </summary>
    /// <param name="eventId">One of the <c>*EventId</c> constants on this class.</param>
    /// <returns>The matching records, in the order they were written.</returns>
    public IReadOnlyList<FakeLogRecord> All(int eventId) =>
        [.. Collector.GetSnapshot().Where(record => record.Id.Id == eventId)];

    /// <summary>
    /// Reads a structured field off a record.
    /// </summary>
    /// <param name="record">The record to read from.</param>
    /// <param name="name">The template placeholder name.</param>
    /// <returns>The rendered value, or <see langword="null"/> when absent or logged as null.</returns>
    public static string? Field(FakeLogRecord record, string name) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;

    /// <summary>
    /// Builds a context carrying whatever a test needs to vary.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="queryString">The query string, leading <c>?</c> included.</param>
    /// <param name="userId">An Entra object id to authenticate as, or <see langword="null"/> for anonymous.</param>
    /// <returns>The context.</returns>
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

    /// <summary>
    /// Replaces the response body feature so writes land somewhere a test can inspect.
    /// </summary>
    /// <param name="context">The context to attach to.</param>
    /// <returns>The stream the response is written to.</returns>
    public static RecordingStream CaptureResponseBody(DefaultHttpContext context)
    {
        var stream = new RecordingStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(stream));

        return stream;
    }
}
