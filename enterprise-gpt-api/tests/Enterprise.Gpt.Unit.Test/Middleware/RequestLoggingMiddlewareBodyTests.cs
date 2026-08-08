using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Middleware;

public sealed class RequestLoggingMiddlewareBodyTests
{
    private readonly RequestLoggingTestHarness _harness = new();

    public RequestLoggingMiddlewareBodyTests()
    {
        _harness.Options.Bodies.LogRequestBody = true;
        _harness.Options.Bodies.LogResponseBody = true;
        _harness.Options.Bodies.ExcludedPaths = ["/api/conversations/*/stream", "/api/documents"];
    }

    [Fact]
    public async Task InvokeAsync_JsonRequestBody_CapturesItAndRedactsSensitiveProperties()
    {
        var context = CreateJsonPost("""{"name":"acme","apiKey":"sk-live-1"}""");

        await _harness.Run(context);

        var payload = _harness.Single(RequestLoggingTestHarness.PayloadEventId);
        var body = RequestLoggingTestHarness.Field(payload, "RequestBody");

        Assert.Equal("""{"name":"acme","apiKey":"[redacted]"}""", body);
        Assert.DoesNotContain("sk-live-1", body);
        Assert.Equal("False", RequestLoggingTestHarness.Field(payload, "Truncated"));
    }

    [Fact]
    public async Task InvokeAsync_RequestBodyCaptured_RewindsTheStreamForModelBinding()
    {
        const string Json = """{"name":"acme"}""";
        var context = CreateJsonPost(Json);
        string? readByThePipeline = null;

        await _harness.Run(context, async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            readByThePipeline = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        });

        Assert.Equal(Json, readByThePipeline);
    }

    [Fact]
    public async Task InvokeAsync_MultipartRequestBody_IsNotCaptured()
    {
        // The allow-list is what keeps a 50 MB document upload out of the telemetry backend.
        var context = CreateJsonPost("binary-ish", contentType: "multipart/form-data; boundary=----x");

        await _harness.Run(context);

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
    }

    [Fact]
    public async Task InvokeAsync_ContentTypeWithACharsetParameter_IsStillCaptured()
    {
        var context = CreateJsonPost("""{"name":"acme"}""", contentType: "application/json; charset=utf-8");

        await _harness.Run(context);

        Assert.Equal(
            """{"name":"acme"}""",
            RequestLoggingTestHarness.Field(_harness.Single(RequestLoggingTestHarness.PayloadEventId), "RequestBody"));
    }

    [Fact]
    public async Task InvokeAsync_RequestBodyLargerThanTheCap_ReportsTheSkipWithoutReadingIt()
    {
        _harness.Options.Bodies.MaxBodyBytes = 16;
        var context = CreateJsonPost(new string('a', 200));

        await _harness.Run(context);

        var payload = _harness.Single(RequestLoggingTestHarness.PayloadEventId);
        var body = RequestLoggingTestHarness.Field(payload, "RequestBody");

        Assert.Contains("[skipped:", body);
        Assert.Contains("exceeds", body);
        Assert.Equal("True", RequestLoggingTestHarness.Field(payload, "Truncated"));
    }

    [Fact]
    public async Task InvokeAsync_ExcludedBodyPathWithAWildcardSegment_CapturesNeitherBody()
    {
        var context = CreateJsonPost("""{"prompt":"private"}""", path: "/api/conversations/abc-123/stream");
        var response = RequestLoggingTestHarness.CaptureResponseBody(context);

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{}", TestContext.Current.CancellationToken);
        });

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
        Assert.Equal("{}", Encoding.UTF8.GetString(response.ToArray()));
    }

    [Fact]
    public async Task InvokeAsync_JsonResponseBody_CapturesIt()
    {
        var context = RequestLoggingTestHarness.CreateContext();
        RequestLoggingTestHarness.CaptureResponseBody(context);

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"id":7,"secret":"hunter2"}""", TestContext.Current.CancellationToken);
        });

        var payload = _harness.Single(RequestLoggingTestHarness.PayloadEventId);
        Assert.Equal("""{"id":7,"secret":"[redacted]"}""", RequestLoggingTestHarness.Field(payload, "ResponseBody"));
        Assert.Equal("application/json", RequestLoggingTestHarness.Field(payload, "ResponseContentType"));
    }

    [Fact]
    public async Task InvokeAsync_ResponseBodyLargerThanTheCap_TruncatesAndFlagsIt()
    {
        _harness.Options.Bodies.MaxBodyBytes = 8;
        var context = RequestLoggingTestHarness.CreateContext();
        var response = RequestLoggingTestHarness.CaptureResponseBody(context);

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("0123456789abcdef", TestContext.Current.CancellationToken);
        });

        var payload = _harness.Single(RequestLoggingTestHarness.PayloadEventId);
        Assert.Equal("01234567", RequestLoggingTestHarness.Field(payload, "ResponseBody"));
        Assert.Equal("True", RequestLoggingTestHarness.Field(payload, "Truncated"));

        // Truncation bounds what is logged, never what the client receives.
        Assert.Equal("0123456789abcdef", Encoding.UTF8.GetString(response.ToArray()));
    }

    [Fact]
    public async Task InvokeAsync_WriteExactlyFillingTheCapFollowedByAnother_FlagsTruncation()
    {
        _harness.Options.Bodies.MaxBodyBytes = 4;
        var context = RequestLoggingTestHarness.CreateContext();
        RequestLoggingTestHarness.CaptureResponseBody(context);

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("abcd", TestContext.Current.CancellationToken);
            await ctx.Response.WriteAsync("efgh", TestContext.Current.CancellationToken);
        });

        var payload = _harness.Single(RequestLoggingTestHarness.PayloadEventId);
        Assert.Equal("abcd", RequestLoggingTestHarness.Field(payload, "ResponseBody"));
        Assert.Equal("True", RequestLoggingTestHarness.Field(payload, "Truncated"));
    }

    [Fact]
    public async Task InvokeAsync_ServerSentEventResponse_IsNotCapturedAndStillStreamsFragmentByFragment()
    {
        // The regression that matters: a wrapper that accumulated instead of forwarding would turn the
        // conversation stream into one delivery at the end.
        var context = RequestLoggingTestHarness.CreateContext("/api/chat", "POST");
        var response = RequestLoggingTestHarness.CaptureResponseBody(context);
        string[] fragments = ["one", "two", "three"];

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";

            foreach (var fragment in fragments)
            {
                await ctx.Response.WriteAsync(fragment, TestContext.Current.CancellationToken);
                await ctx.Response.Body.FlushAsync(TestContext.Current.CancellationToken);
            }
        });

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
        Assert.Equal("onetwothree", Encoding.UTF8.GetString(response.ToArray()));

        // One write per fragment, in order: nothing was held back and coalesced at the end.
        Assert.Equal([3, 3, 5], response.WriteSizes);

        // At least one flush per fragment — the response pipe writer adds its own on top, so the count is
        // a floor rather than an equality. A wrapper that swallowed FlushAsync would show none.
        Assert.True(response.FlushCount >= fragments.Length, $"expected at least {fragments.Length} flushes, saw {response.FlushCount}");
    }

    [Fact]
    public async Task InvokeAsync_OctetStreamResponse_IsNotCaptured()
    {
        var context = RequestLoggingTestHarness.CreateContext();
        RequestLoggingTestHarness.CaptureResponseBody(context);

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "application/octet-stream";
            await ctx.Response.WriteAsync("binary", TestContext.Current.CancellationToken);
        });

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
    }

    [Fact]
    public async Task InvokeAsync_ResponseCaptureInstalled_RestoresTheOriginalFeature()
    {
        var context = RequestLoggingTestHarness.CreateContext();
        var original = RequestLoggingTestHarness.CaptureResponseBody(context);
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();

        await _harness.Run(context, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{}", TestContext.Current.CancellationToken);
        });

        Assert.Same(originalFeature, context.Features.Get<IHttpResponseBodyFeature>());
        Assert.Equal("{}", Encoding.UTF8.GetString(original.ToArray()));
    }

    [Fact]
    public async Task InvokeAsync_PipelineThrowsAfterCaptureInstalled_StillRestoresTheOriginalFeature()
    {
        var context = RequestLoggingTestHarness.CreateContext();
        RequestLoggingTestHarness.CaptureResponseBody(context);
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.Run(context, _ => throw new InvalidOperationException("boom")));

        Assert.Same(originalFeature, context.Features.Get<IHttpResponseBodyFeature>());
    }

    [Fact]
    public async Task InvokeAsync_PayloadLevelConfigured_WritesThePayloadRecordAtThatLevel()
    {
        // The second gate: the switch turns capture on, the level decides who ever sees it. Dropping it
        // to Debug means a category filter has to admit Debug as well before payloads are emitted.
        _harness.Options.Bodies.LogLevel = LogLevel.Debug;

        await _harness.Run(CreateJsonPost("""{"name":"acme"}"""));

        Assert.Equal(LogLevel.Debug, _harness.Single(RequestLoggingTestHarness.PayloadEventId).Level);
    }

    [Fact]
    public async Task InvokeAsync_EmptyRequestBody_WritesNoPayloadRecord()
    {
        var context = RequestLoggingTestHarness.CreateContext("/api/models", "POST");
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = 0;

        await _harness.Run(context);

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
    }

    private static DefaultHttpContext CreateJsonPost(
        string body,
        string path = "/api/models",
        string contentType = "application/json")
    {
        var context = RequestLoggingTestHarness.CreateContext(path, "POST");
        var bytes = Encoding.UTF8.GetBytes(body);

        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;

        return context;
    }
}
