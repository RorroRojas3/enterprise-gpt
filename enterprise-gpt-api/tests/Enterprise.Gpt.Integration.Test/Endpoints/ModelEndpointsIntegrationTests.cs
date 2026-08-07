using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Model;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetModelsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static CreateModelActionDto CreateRequest(
        Guid? providerId = null, string name = "it-created", bool isDefault = false)
    {
        return new CreateModelActionDto
        {
            ProviderId = providerId ?? KnownIds.SeedProviderId,
            Name = name,
            DisplayName = $"{name} display",
            Description = $"{name} description",
            ContextWindowSize = 128000m,
            MaxOutputTokens = 16384m,
            IsToolEnabled = true,
            IsDefault = isDefault
        };
    }

    private static UpdateModelActionDto UpdateRequest(
        Guid? providerId = null, string name = "it-updated", bool isDefault = false)
    {
        return new UpdateModelActionDto
        {
            ProviderId = providerId ?? KnownIds.SeedProviderId,
            Name = name,
            DisplayName = $"{name} display",
            Description = $"{name} description",
            ContextWindowSize = 64000m,
            MaxOutputTokens = 8192m,
            IsToolEnabled = false,
            IsDefault = isDefault
        };
    }


    [Fact]
    public async Task GetModels_AnonymousUser_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/models", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task GetModels_RegularUser_ReturnsActiveModelsOrderedByName()
    {
        var activeId = await _fixture.AddModelAsync("it-aaa-active", cancellationToken: TestContext.Current.CancellationToken);
        var deactivatedId = await _fixture.AddModelAsync("it-bbb-inactive", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var models = await client.GetFromJsonAsync<List<ModelDto>>("api/models", TestContext.Current.CancellationToken);

        Assert.NotNull(models);
        Assert.Contains(models, x => x.Id == activeId);
        Assert.Contains(models, x => x.Id == KnownIds.SeedModelId);
        Assert.DoesNotContain(models, x => x.Id == deactivatedId);
        Assert.All(models, x => Assert.Null(x.DateDeactivated));
        Assert.Equal(models.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Id), models.Select(x => x.Id));
    }

    [Fact]
    public async Task GetAllModels_RegularUser_ReturnsForbidden()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/models/all", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task GetAllModels_AdminUser_IncludesDeactivatedModels()
    {
        var deactivatedId = await _fixture.AddModelAsync("it-bbb-inactive", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var models = await client.GetFromJsonAsync<List<ModelDto>>("api/models/all", TestContext.Current.CancellationToken);

        Assert.NotNull(models);
        var deactivated = Assert.Single(models, x => x.Id == deactivatedId);
        Assert.NotNull(deactivated.DateDeactivated);
        Assert.Contains(models, x => x.Id == KnownIds.SeedModelId);
    }

    [Fact]
    public async Task GetModel_SeededModel_ReturnsModel()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var model = await client.GetFromJsonAsync<ModelDto>(
            $"api/models/{KnownIds.SeedModelId}", TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Equal(KnownIds.SeedModelId, model.Id);
        Assert.Equal("rr-gpt-5.6-luna", model.Name);
        Assert.Equal(KnownIds.SeedProviderId, model.ProviderId);
    }

    [Fact]
    public async Task GetModel_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/models/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Model not found.", problem.Detail);
    }

    [Fact]
    public async Task GetModel_AcceptHeaderTheProblemWriterRejects_StillReturnsABody()
    {
        // DefaultProblemDetailsWriter declines anything outside JSON and writes nothing, so the
        // handlers fall back to an explicit write. Without it a browser hitting an API URL directly
        // would get a bare status.
        using var client = _fixture.Factory.CreateUserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/models/{Guid.NewGuid()}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Model not found.", problem.Detail);
        Assert.False(string.IsNullOrWhiteSpace(problem.TraceId));
    }

    [Fact]
    public async Task GetModel_NonGuidId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/models/not-a-guid", TestContext.Current.CancellationToken);

        // No route matches, so this 404 comes from the status code pages middleware rather than an
        // endpoint — hence the RFC 9110 title instead of a domain problem type.
        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Not Found", problem.Title);
    }

    [Fact]
    public async Task CreateModel_AdminUser_ReturnsCreatedAndPersistsAudit()
    {
        var request = CreateRequest(name: "it-created-audit");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/models", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ModelDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(request.Name, created.Name);
        Assert.Equal($"/api/models/{created.Id}", response.Headers.Location?.ToString());

        var persisted = await _fixture.FindModelAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(TestUsers.AdminUserId, persisted.CreatedById);
        Assert.Equal(TestUsers.AdminUserId, persisted.ModifiedById);
        Assert.Null(persisted.DateDeactivated);
    }

    [Fact]
    public async Task CreateModel_InvalidBody_ReturnsBadRequestWithValidationErrors()
    {
        var request = CreateRequest() with { Name = string.Empty, ContextWindowSize = 0m };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/models", request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.True(problem.Errors.Count >= 2);
    }

    [Fact]
    public async Task CreateModel_UnknownProvider_ReturnsNotFound()
    {
        var request = CreateRequest(providerId: Guid.NewGuid());
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/models", request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Provider not found.", problem.Detail);
    }

    [Fact]
    public async Task CreateModel_RegularUser_ReturnsForbidden()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync("api/models", CreateRequest(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task CreateModel_SecondDefault_DemotesPreviousDefault()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var firstResponse = await client.PostAsJsonAsync(
            "api/models", CreateRequest(name: "it-default-a", isDefault: true), TestContext.Current.CancellationToken);
        var first = await firstResponse.Content.ReadFromJsonAsync<ModelDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        var secondResponse = await client.PostAsJsonAsync(
            "api/models", CreateRequest(name: "it-default-b", isDefault: true), TestContext.Current.CancellationToken);
        var second = await secondResponse.Content.ReadFromJsonAsync<ModelDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(second);

        var models = await client.GetFromJsonAsync<List<ModelDto>>("api/models/all", TestContext.Current.CancellationToken);
        Assert.NotNull(models);
        var currentDefault = Assert.Single(models, x => x.IsDefault);
        Assert.Equal(second.Id, currentDefault.Id);
    }

    [Fact]
    public async Task UpdateModel_AdminUser_UpdatesFields()
    {
        var id = await _fixture.AddModelAsync("it-original", cancellationToken: TestContext.Current.CancellationToken);
        var request = UpdateRequest(name: "it-renamed");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync($"api/models/{id}", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ModelDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(request.Name, updated.Name);

        var fetched = await client.GetFromJsonAsync<ModelDto>($"api/models/{id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(request.Name, fetched.Name);
        Assert.Equal(request.DisplayName, fetched.DisplayName);
        Assert.Equal(request.Description, fetched.Description);
        Assert.Equal(request.ContextWindowSize, fetched.ContextWindowSize);
        Assert.Equal(request.MaxOutputTokens, fetched.MaxOutputTokens);
        Assert.Equal(request.IsToolEnabled, fetched.IsToolEnabled);
    }

    [Fact]
    public async Task UpdateModel_InvalidBody_ReturnsBadRequest()
    {
        var id = await _fixture.AddModelAsync("it-original", cancellationToken: TestContext.Current.CancellationToken);
        var request = UpdateRequest() with { DisplayName = string.Empty, MaxOutputTokens = 0m };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync($"api/models/{id}", request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.True(problem.Errors.Count >= 2);
    }

    [Fact]
    public async Task UpdateModel_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/models/{Guid.NewGuid()}", UpdateRequest(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Model not found.", problem.Detail);
    }

    [Fact]
    public async Task UpdateModel_UnknownProvider_ReturnsNotFound()
    {
        var id = await _fixture.AddModelAsync("it-original", cancellationToken: TestContext.Current.CancellationToken);
        var request = UpdateRequest(providerId: Guid.NewGuid());
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync($"api/models/{id}", request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Provider not found.", problem.Detail);
    }

    [Fact]
    public async Task UpdateModel_RegularUser_ReturnsForbidden()
    {
        var id = await _fixture.AddModelAsync("it-original", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync($"api/models/{id}", UpdateRequest(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task UpdateModel_SetDefault_DemotesOtherDefault()
    {
        var defaultId = await _fixture.AddModelAsync("it-default-a", isDefault: true, cancellationToken: TestContext.Current.CancellationToken);
        var promotedId = await _fixture.AddModelAsync("it-default-b", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/models/{promotedId}", UpdateRequest(name: "it-default-b", isDefault: true), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = await client.GetFromJsonAsync<List<ModelDto>>("api/models/all", TestContext.Current.CancellationToken);
        Assert.NotNull(models);
        var currentDefault = Assert.Single(models, x => x.IsDefault);
        Assert.Equal(promotedId, currentDefault.Id);
        Assert.False(models.Single(x => x.Id == defaultId).IsDefault);
    }

    [Fact]
    public async Task DeleteModel_AdminUser_SoftDeletesAndClearsDefault()
    {
        var id = await _fixture.AddModelAsync("it-doomed", isDefault: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync($"api/models/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"api/models/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        var models = await client.GetFromJsonAsync<List<ModelDto>>("api/models/all", TestContext.Current.CancellationToken);
        Assert.NotNull(models);
        var deleted = Assert.Single(models, x => x.Id == id);
        Assert.NotNull(deleted.DateDeactivated);
        Assert.False(deleted.IsDefault);
    }

    [Fact]
    public async Task DeleteModel_SecondDelete_ReturnsNotFound()
    {
        var id = await _fixture.AddModelAsync("it-doomed", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();
        var firstResponse = await client.DeleteAsync($"api/models/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await client.DeleteAsync($"api/models/{id}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(secondResponse, HttpStatusCode.NotFound);
        Assert.Equal("Model not found.", problem.Detail);
    }

    [Fact]
    public async Task DeleteModel_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync($"api/models/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Model not found.", problem.Detail);
    }

    [Fact]
    public async Task DeleteModel_RegularUser_ReturnsForbidden()
    {
        var id = await _fixture.AddModelAsync("it-protected", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/models/{id}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }
}
