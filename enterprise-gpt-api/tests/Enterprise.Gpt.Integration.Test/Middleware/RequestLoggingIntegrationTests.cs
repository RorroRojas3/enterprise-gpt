using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Middleware;

/// <summary>
/// Covers what unit tests cannot: that the middleware is actually in the pipeline, and in a position
/// where it sees the status the client received.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RequestLoggingIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const int CompletedEventId = 1001;

    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetModelsAsync(TestContext.Current.CancellationToken);
        _fixture.Factory.ClearRequestLogs();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Request_AuthenticatedAndSuccessful_IsLoggedAtInformationWithTheRouteAndCaller()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync("api/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var completed = SingleCompletion();
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("200", Field(completed, "StatusCode"));
        Assert.Equal("Completed", Field(completed, "Outcome"));
        // The group prefix plus MapGet("") really does produce a trailing slash; the point of logging the
        // template rather than the path is that it is the low-cardinality dimension, not that it is tidy.
        Assert.Equal("api/models/", Field(completed, "RouteTemplate"));
        Assert.Equal(TestUsers.AdminUserId.ToString(), Field(completed, "UserId"));
    }

    [Fact]
    public async Task Request_Anonymous_IsLoggedAtWarningWithThe401()
    {
        // Only reachable because the middleware sits ahead of UseAuthentication: the challenge status is
        // written by middleware further in and has to be observable on the way back out.
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var completed = SingleCompletion();
        Assert.Equal(LogLevel.Warning, completed.Level);
        Assert.Equal("401", Field(completed, "StatusCode"));
        Assert.Null(Field(completed, "UserId"));
    }

    [Fact]
    public async Task Request_DeniedByThePermissionFilter_IsLoggedAtWarningWithThe403()
    {
        // The gap this middleware closes: PermissionEndpointFilter writes its 403 directly rather than
        // throwing, so no exception handler ever sees it and nothing logged it before.
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/models",
            new { name = "denied", providerId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var completed = SingleCompletion();
        Assert.Equal(LogLevel.Warning, completed.Level);
        Assert.Equal("403", Field(completed, "StatusCode"));
        Assert.Equal(TestUsers.RegularUserId.ToString(), Field(completed, "UserId"));
    }

    [Fact]
    public async Task Request_UnroutedPath_IsLoggedAtWarningWithNoRouteTemplate()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync("api/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var completed = SingleCompletion();
        Assert.Equal(LogLevel.Warning, completed.Level);
        Assert.Equal("404", Field(completed, "StatusCode"));
        Assert.Null(Field(completed, "RouteTemplate"));
    }

    [Fact]
    public async Task Request_BodiesDisabledByDefault_LogsNoPayloadRecord()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        await client.GetAsync("api/models", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_fixture.Factory.GetRequestLogs(), record => record.Id.Id == 1002);
    }

    private FakeLogRecord SingleCompletion() =>
        Assert.Single(_fixture.Factory.GetRequestLogs(), record => record.Id.Id == CompletedEventId);

    private static string? Field(FakeLogRecord record, string name) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;
}
