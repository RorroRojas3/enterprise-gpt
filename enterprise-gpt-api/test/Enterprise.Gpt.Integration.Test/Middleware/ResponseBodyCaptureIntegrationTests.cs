using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Middleware;

/// <summary>
/// Exercises response body capture against a real host, where the swapped
/// <c>IHttpResponseBodyFeature</c> has a genuine prior feature behind it — the pipe writer, the
/// completion handshake and the restore all run for real, which a <c>DefaultHttpContext</c> cannot show.
/// </summary>
/// <remarks>
/// Runs on its own host because body capture is off by default and the shared factory is used by the rest
/// of the suite.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ResponseBodyCaptureIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const int PayloadEventId = 1002;
    private const int LoggingFailedEventId = 1004;

    private readonly IntegrationTestFixture _fixture = fixture;

    private WebApplicationFactory<Program> _factory = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetModelsAsync(TestContext.Current.CancellationToken);

        _factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RequestLogging:Bodies:LogResponseBody", "true");
            builder.UseSetting("RequestLogging:Bodies:LogRequestBody", "true");
            // The default excludes every conversation and document route; this host needs a route it
            // will actually capture, and api/models/all returns ordinary JSON with no user content in it.
            builder.UseSetting("RequestLogging:Bodies:ExcludedPaths:0", "/api/conversations");
        });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _factory?.Dispose();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Response_WithCaptureEnabled_ReachesTheClientIntactAndIsLogged()
    {
        using var client = CreateAdminClient();

        // models/all rather than models: the seeded row is the summarizer, which the picker's
        // own route now filters out, and this test needs a body with a known id in it.
        var response = await client.GetAsync("api/models/all", TestContext.Current.CancellationToken);
        var models = await response.Content.ReadFromJsonAsync<List<ModelDto>>(TestContext.Current.CancellationToken);

        // The response the client received is the thing that matters; capture must be invisible to it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(models);
        Assert.Contains(models, model => model.Id == KnownIds.SeedModelId);

        var payload = Assert.Single(Logs(), record => record.Id.Id == PayloadEventId);
        var body = Field(payload, "ResponseBody");

        Assert.NotNull(body);
        Assert.Contains(KnownIds.SeedModelId.ToString(), body);
        Assert.DoesNotContain(Logs(), record => record.Id.Id == LoggingFailedEventId);
    }

    [Fact]
    public async Task Response_ProblemDetails_IsCapturedWithoutDisturbingTheProblemBody()
    {
        // Problem responses are written through IProblemDetailsService rather than the usual result
        // pipeline, so they exercise a different path to the response stream.
        using var client = CreateAdminClient();

        var response = await client.GetAsync(
            $"api/models/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("traceId", problem);

        var payload = Assert.Single(Logs(), record => record.Id.Id == PayloadEventId);
        Assert.Contains("traceId", Field(payload, "ResponseBody")!);
    }

    [Fact]
    public async Task Request_WithCaptureEnabled_StillModelBindsTheBody()
    {
        // Capture rewinds the request stream; if it ever stopped, the endpoint would bind a truncated
        // payload and this would surface as a validation failure rather than a 403.
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "user");

        var response = await client.PostAsJsonAsync(
            "api/models",
            new { name = "capture-probe", providerId = KnownIds.SeedProviderId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = Assert.Single(Logs(), record => record.Id.Id == PayloadEventId);
        Assert.Contains("capture-probe", Field(payload, "RequestBody")!);
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "admin");

        return client;
    }

    private IReadOnlyList<FakeLogRecord> Logs() =>
        _factory.Services.GetRequiredService<FakeLogCollector>().GetSnapshot();

    private static string? Field(FakeLogRecord record, string name) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;
}
