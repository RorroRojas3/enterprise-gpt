using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Middleware;

public sealed class RequestLoggingMiddlewareTests
{
    private readonly RequestLoggingTestHarness _harness = new();

    [Fact]
    public async Task InvokeAsync_SuccessfulRequest_LogsStartAndCompletion()
    {
        var context = RequestLoggingTestHarness.CreateContext();

        await _harness.Run(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var started = _harness.Single(RequestLoggingTestHarness.StartedEventId);
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Equal("GET", RequestLoggingTestHarness.Field(started, "RequestMethod"));
        Assert.Equal("/api/models", RequestLoggingTestHarness.Field(started, "RequestPath"));

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("200", RequestLoggingTestHarness.Field(completed, "StatusCode"));
        Assert.Equal("Completed", RequestLoggingTestHarness.Field(completed, "Outcome"));
        Assert.Null(completed.Exception);
    }

    [Fact]
    public async Task InvokeAsync_RequestStartLevelLowered_LogsTheStartRecordAtThatLevel()
    {
        _harness.Options.RequestStartLevel = LogLevel.Debug;

        await _harness.Run(RequestLoggingTestHarness.CreateContext());

        Assert.Equal(LogLevel.Debug, _harness.Single(RequestLoggingTestHarness.StartedEventId).Level);
        Assert.Equal(LogLevel.Information, _harness.Single(RequestLoggingTestHarness.CompletedEventId).Level);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, LogLevel.Warning)]
    [InlineData(StatusCodes.Status401Unauthorized, LogLevel.Warning)]
    [InlineData(StatusCodes.Status403Forbidden, LogLevel.Warning)]
    [InlineData(StatusCodes.Status404NotFound, LogLevel.Warning)]
    [InlineData(StatusCodes.Status409Conflict, LogLevel.Warning)]
    [InlineData(StatusCodes.Status500InternalServerError, LogLevel.Error)]
    [InlineData(StatusCodes.Status502BadGateway, LogLevel.Error)]
    [InlineData(StatusCodes.Status204NoContent, LogLevel.Information)]
    public async Task InvokeAsync_ResponseStatus_SelectsTheMatchingCompletionLevel(int statusCode, LogLevel expected)
    {
        await _harness.Run(RequestLoggingTestHarness.CreateContext(), ctx =>
        {
            ctx.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(expected, completed.Level);
        Assert.Equal("Completed", RequestLoggingTestHarness.Field(completed, "Outcome"));
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulRequestSlowerThanTheThreshold_LogsCompletionAtWarning()
    {
        _harness.Options.SlowRequestThreshold = TimeSpan.FromSeconds(3);

        await _harness.Run(RequestLoggingTestHarness.CreateContext(), _ =>
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        });

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(LogLevel.Warning, completed.Level);
        Assert.Equal("4000", RequestLoggingTestHarness.Field(completed, "ElapsedMilliseconds"));
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulRequestInsideTheThreshold_LogsCompletionAtInformation()
    {
        _harness.Options.SlowRequestThreshold = TimeSpan.FromSeconds(3);

        await _harness.Run(RequestLoggingTestHarness.CreateContext(), _ =>
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Information, _harness.Single(RequestLoggingTestHarness.CompletedEventId).Level);
    }

    [Fact]
    public async Task InvokeAsync_PipelineThrows_LogsTheFaultAndRethrows()
    {
        var failure = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.Run(RequestLoggingTestHarness.CreateContext(), _ => throw failure));

        Assert.Same(failure, thrown);

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(LogLevel.Error, completed.Level);
        Assert.Equal("Faulted", RequestLoggingTestHarness.Field(completed, "Outcome"));
        Assert.Same(failure, completed.Exception);
    }

    [Fact]
    public async Task InvokeAsync_ClientAbandonedTheRequest_LogsCompletionAtInformation()
    {
        // Ordinary traffic for this API: a user navigating away mid-stream, already mapped to a 499.
        var context = RequestLoggingTestHarness.CreateContext();
        context.RequestAborted = new CancellationToken(canceled: true);

        await _harness.Run(context);

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("ClientAborted", RequestLoggingTestHarness.Field(completed, "Outcome"));
    }

    [Fact]
    public async Task InvokeAsync_ExcludedPathSucceeds_LogsNothing()
    {
        _harness.Options.ExcludedPaths = ["/health"];

        await _harness.Run(RequestLoggingTestHarness.CreateContext("/health/ready"));

        Assert.Empty(_harness.Collector.GetSnapshot());
    }

    [Fact]
    public async Task InvokeAsync_ExcludedPathFails_LogsTheCompletionRecordOnly()
    {
        _harness.Options.ExcludedPaths = ["/health"];

        await _harness.Run(RequestLoggingTestHarness.CreateContext("/health"), ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        Assert.Empty(_harness.All(RequestLoggingTestHarness.StartedEventId));
        Assert.Equal(LogLevel.Error, _harness.Single(RequestLoggingTestHarness.CompletedEventId).Level);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedCaller_LogsTheEntraObjectId()
    {
        var userId = Guid.NewGuid();

        await _harness.Run(RequestLoggingTestHarness.CreateContext(userId: userId));

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal(userId.ToString(), RequestLoggingTestHarness.Field(completed, "UserId"));
    }

    [Fact]
    public async Task InvokeAsync_AnonymousCaller_LogsNoUserIdAndDoesNotThrow()
    {
        // ITokenService.GetOid throws when unauthenticated, and an anonymous request is one of the cases
        // most worth logging — so the claim has to be read off the principal directly.
        await _harness.Run(RequestLoggingTestHarness.CreateContext(), ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Null(RequestLoggingTestHarness.Field(completed, "UserId"));
        Assert.Equal("401", RequestLoggingTestHarness.Field(completed, "StatusCode"));
    }

    [Fact]
    public async Task InvokeAsync_RoutedRequest_LogsTheRouteTemplateAndEndpointName()
    {
        var context = RequestLoggingTestHarness.CreateContext("/api/models/42");
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/models/{id}"),
            order: 0,
            metadata: null,
            displayName: "GetModel"));

        await _harness.Run(context);

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Equal("api/models/{id}", RequestLoggingTestHarness.Field(completed, "RouteTemplate"));
        Assert.Equal("GetModel", RequestLoggingTestHarness.Field(completed, "EndpointName"));
    }

    [Fact]
    public async Task InvokeAsync_UnroutedRequest_LogsNoRouteTemplate()
    {
        await _harness.Run(RequestLoggingTestHarness.CreateContext("/nope"), ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        var completed = _harness.Single(RequestLoggingTestHarness.CompletedEventId);
        Assert.Null(RequestLoggingTestHarness.Field(completed, "RouteTemplate"));
        Assert.Equal("/nope", RequestLoggingTestHarness.Field(completed, "RequestPath"));
    }

    [Fact]
    public async Task InvokeAsync_QueryStringWithAnUnlistedKey_RedactsOnlyItsValue()
    {
        var context = RequestLoggingTestHarness.CreateContext(
            "/api/conversations/search",
            queryString: "?name=confidential&take=20");

        await _harness.Run(context);

        var path = RequestLoggingTestHarness.Field(
            _harness.Single(RequestLoggingTestHarness.CompletedEventId), "RequestPath");

        Assert.Equal("/api/conversations/search?name=[redacted]&take=20", path);
        Assert.DoesNotContain("confidential", path);
    }

    [Fact]
    public async Task InvokeAsync_QueryRedactionDisabled_LogsTheQueryStringVerbatim()
    {
        _harness.Options.RedactQueryValues = false;
        var context = RequestLoggingTestHarness.CreateContext("/api/conversations/search", queryString: "?name=confidential");

        await _harness.Run(context);

        Assert.Equal(
            "/api/conversations/search?name=confidential",
            RequestLoggingTestHarness.Field(_harness.Single(RequestLoggingTestHarness.CompletedEventId), "RequestPath"));
    }

    [Fact]
    public async Task InvokeAsync_NoQueryString_LogsThePathAlone()
    {
        await _harness.Run(RequestLoggingTestHarness.CreateContext("/api/models"));

        Assert.Equal(
            "/api/models",
            RequestLoggingTestHarness.Field(_harness.Single(RequestLoggingTestHarness.CompletedEventId), "RequestPath"));
    }

    [Fact]
    public async Task InvokeAsync_ClientIpCollectionDisabled_OmitsTheAddress()
    {
        var context = RequestLoggingTestHarness.CreateContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        context.Request.Headers.UserAgent = "test-agent";

        await _harness.Run(context);

        var started = _harness.Single(RequestLoggingTestHarness.StartedEventId);
        Assert.Null(RequestLoggingTestHarness.Field(started, "ClientIp"));
        Assert.Equal("test-agent", RequestLoggingTestHarness.Field(started, "UserAgent"));
    }

    [Fact]
    public async Task InvokeAsync_ClientIpCollectionEnabled_LogsTheAddress()
    {
        _harness.Options.IncludeClientIp = true;
        _harness.Options.IncludeUserAgent = false;

        var context = RequestLoggingTestHarness.CreateContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        context.Request.Headers.UserAgent = "test-agent";

        await _harness.Run(context);

        var started = _harness.Single(RequestLoggingTestHarness.StartedEventId);
        Assert.Equal("203.0.113.7", RequestLoggingTestHarness.Field(started, "ClientIp"));
        Assert.Null(RequestLoggingTestHarness.Field(started, "UserAgent"));
    }

    [Fact]
    public async Task InvokeAsync_ResponseAlreadyStarted_LogsWithoutThrowing()
    {
        // The middleware must never touch headers on the way out: a streaming response has already
        // committed them by then, and assigning would throw over the top of the real failure.
        var context = RequestLoggingTestHarness.CreateContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        await _harness.Run(context, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        Assert.Equal(LogLevel.Information, _harness.Single(RequestLoggingTestHarness.CompletedEventId).Level);
    }

    [Fact]
    public async Task InvokeAsync_BodyLoggingDisabled_LeavesTheRequestAndResponseStreamsAlone()
    {
        var context = RequestLoggingTestHarness.CreateContext("/api/models", "POST");
        var body = new MemoryStream("{\"name\":\"acme\"}"u8.ToArray());
        context.Request.Body = body;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;

        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();

        await _harness.Run(context);

        Assert.Same(body, context.Request.Body);
        Assert.Equal(0, body.Position);
        Assert.Same(originalFeature, context.Features.Get<IHttpResponseBodyFeature>());
        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }
    }
}
