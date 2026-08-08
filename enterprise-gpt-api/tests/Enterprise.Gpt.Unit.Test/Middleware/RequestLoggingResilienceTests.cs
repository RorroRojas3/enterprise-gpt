using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Middleware;

/// <summary>
/// The middleware's core promise: it never fails a request and never alters one — plus the registration
/// behaviour that only shows up through the container.
/// </summary>
public sealed class RequestLoggingResilienceTests
{
    private readonly RequestLoggingTestHarness _harness = new();

    [Fact]
    public async Task InvokeAsync_RedactorThrows_DoesNotFailTheRequest()
    {
        // IRequestBodyRedactor is a swappable extension point, and it runs inside the finally block —
        // where an escaping exception would replace whatever the pipeline was already reporting.
        _harness.Options.Bodies.LogRequestBody = true;
        _harness.Redactor = new ThrowingRedactor();

        var context = CreateJsonPost("""{"name":"acme"}""");

        await _harness.Run(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Single(_harness.All(RequestLoggingTestHarness.LoggingFailedEventId));
    }

    [Fact]
    public async Task InvokeAsync_RedactorThrowsWhileThePipelineIsFaulting_PreservesTheOriginalException()
    {
        // The worse half of the same problem: a logging failure must not mask the real fault before
        // UseExceptionHandler ever sees it.
        _harness.Options.Bodies.LogRequestBody = true;
        _harness.Redactor = new ThrowingRedactor();

        var failure = new InvalidOperationException("the real fault");
        var context = CreateJsonPost("""{"name":"acme"}""");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _harness.Run(context, _ => throw failure));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task InvokeAsync_RequestBodyReadFails_RewindsTheStreamForModelBinding()
    {
        // A read that threw part-way has already consumed bytes; handing the endpoint a truncated body to
        // model-bind would be the middleware silently corrupting the request.
        _harness.Options.Bodies.LogRequestBody = true;

        var context = RequestLoggingTestHarness.CreateContext("/api/models", "POST");
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = 64;
        context.Request.Body = new ThrowAfterFirstReadStream("""{"name":"acme"}"""u8.ToArray());

        long? positionSeenByThePipeline = null;

        await _harness.Run(context, ctx =>
        {
            positionSeenByThePipeline = ctx.Request.Body.Position;
            return Task.CompletedTask;
        });

        Assert.Equal(0L, positionSeenByThePipeline);
        Assert.Single(_harness.All(RequestLoggingTestHarness.CaptureFailedEventId));
        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
    }

    [Fact]
    public async Task InvokeAsync_ChunkedRequestWithNoContentLength_SkipsCapture()
    {
        // A chunked body declares no length, so its size is unknown until it has been read — the same
        // problem EnableBuffering makes expensive for an oversized one.
        _harness.Options.Bodies.LogRequestBody = true;

        var context = RequestLoggingTestHarness.CreateContext("/api/models", "POST");
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = null;
        var body = new MemoryStream("""{"name":"acme"}"""u8.ToArray());
        context.Request.Body = body;

        await _harness.Run(context);

        Assert.Empty(_harness.All(RequestLoggingTestHarness.PayloadEventId));
        Assert.Same(body, context.Request.Body);
    }

    [Fact]
    public async Task InvokeAsync_ExcludedPathWithBodyCaptureOn_WritesNoPayloadRecord()
    {
        _harness.Options.ExcludedPaths = ["/health"];
        _harness.Options.Bodies.LogRequestBody = true;
        _harness.Options.Bodies.ExcludedPaths = [];

        await _harness.Run(CreateJsonPost("""{"name":"acme"}""", path: "/health"));

        Assert.Empty(_harness.Collector.GetSnapshot());
    }

    [Theory]
    [InlineData("/api/x\ninjected", "/api/x injected")]
    [InlineData("/api/x\r\nHTTP GET /admin", "/api/x  HTTP GET /admin")]
    [InlineData("/api/x\tspaced", "/api/x spaced")]
    public async Task InvokeAsync_PathContainingControlCharacters_LogsThemNeutralized(string path, string expected)
    {
        // Path.Value is percent-decoded, so %0A arrives as a real newline and would forge a second line
        // in any text-based sink (CWE-117).
        await _harness.Run(RequestLoggingTestHarness.CreateContext(path));

        Assert.Equal(
            expected,
            RequestLoggingTestHarness.Field(_harness.Single(RequestLoggingTestHarness.CompletedEventId), "RequestPath"));
    }

    [Fact]
    public async Task InvokeAsync_QueryKeyContainingControlCharacters_LogsThemNeutralized()
    {
        var context = RequestLoggingTestHarness.CreateContext("/api/models", queryString: "?bad%0Akey=value");

        await _harness.Run(context);

        var loggedPath = RequestLoggingTestHarness.Field(
            _harness.Single(RequestLoggingTestHarness.CompletedEventId), "RequestPath");

        Assert.DoesNotContain('\n', loggedPath!);
        Assert.Contains("bad key=[redacted]", loggedPath);
    }

    [Fact]
    public async Task UseRequestLogging_Disabled_LeavesTheMiddlewareOutOfThePipeline()
    {
        var (pipeline, collector, services) = BuildPipeline(
            new Dictionary<string, string?> { ["RequestLogging:Enabled"] = "false" });

        await pipeline(new DefaultHttpContext { RequestServices = services });

        Assert.Empty(collector.GetSnapshot());
    }

    [Fact]
    public async Task UseRequestLogging_Enabled_AddsTheMiddlewareToThePipeline()
    {
        var (pipeline, collector, services) = BuildPipeline([]);

        await pipeline(new DefaultHttpContext { RequestServices = services });

        Assert.Contains(collector.GetSnapshot(), record => record.Id.Id == RequestLoggingTestHarness.CompletedEventId);
    }

    [Theory]
    [InlineData("RequestLogging:SlowRequestThreshold", "-00:00:01")]
    [InlineData("RequestLogging:Bodies:MaxBodyBytes", "0")]
    [InlineData("RequestLogging:Bodies:MaxBodyBytes", "2097152")]
    [InlineData("RequestLogging:ExcludedPaths:0", "health")]
    [InlineData("RequestLogging:Bodies:ExcludedPaths:0", "api/documents")]
    public void AddRequestLogging_InvalidConfiguration_FailsWhenTheOptionsAreResolved(string key, string value)
    {
        var provider = BuildProvider(new Dictionary<string, string?> { [key] = value });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RequestLoggingOptions>>().Value);
    }

    [Fact]
    public void AddRequestLogging_ConfiguredList_ReplacesTheDefaultRatherThanExtendingIt()
    {
        // The configuration binder binds a collection into the existing instance, so an inline default
        // would make an allow-list impossible to narrow — only to widen.
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["RequestLogging:Bodies:AllowedContentTypes:0"] = "text/plain"
        });

        var options = provider.GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

        Assert.Equal(new[] { "text/plain" }, options.Bodies.AllowedContentTypes);
    }

    [Fact]
    public void AddRequestLogging_NoConfiguredLists_AppliesTheDocumentedDefaults()
    {
        var options = BuildProvider([]).GetRequiredService<IOptions<RequestLoggingOptions>>().Value;

        Assert.Equal(new[] { "/health", "/openapi", "/scalar" }, options.ExcludedPaths);
        Assert.Contains("application/json", options.Bodies.AllowedContentTypes);
        Assert.Contains("/api/conversations", options.Bodies.ExcludedPaths);
        Assert.Contains("password", options.Bodies.RedactedPropertyNames);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddFakeLogging();
        services.AddRequestLogging(configuration);

        return services.BuildServiceProvider();
    }

    private static (RequestDelegate Pipeline, FakeLogCollector Collector, IServiceProvider Services) BuildPipeline(
        Dictionary<string, string?> settings)
    {
        var provider = BuildProvider(settings);
        var app = new ApplicationBuilder(provider);
        app.UseRequestLogging();
        app.Run(_ => Task.CompletedTask);

        return (app.Build(), provider.GetRequiredService<FakeLogCollector>(), provider);
    }

    private static DefaultHttpContext CreateJsonPost(string body, string path = "/api/models")
    {
        var context = RequestLoggingTestHarness.CreateContext(path, "POST");
        var bytes = Encoding.UTF8.GetBytes(body);

        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        return context;
    }

    private sealed class ThrowingRedactor : IRequestBodyRedactor
    {
        public string Redact(string body, string? contentType) => throw new InvalidOperationException("redactor blew up");
    }

    /// <summary>
    /// Consumes bytes and then throws, standing in for a body that faults part-way through being read.
    /// </summary>
    private sealed class ThrowAfterFirstReadStream(byte[] content) : MemoryStream(content)
    {
        private readonly int _length = content.Length;
        private bool _consumed;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_consumed)
            {
                throw new IOException("connection reset");
            }

            _consumed = true;
            Position = _length;

            return ValueTask.FromResult(_length);
        }
    }
}
