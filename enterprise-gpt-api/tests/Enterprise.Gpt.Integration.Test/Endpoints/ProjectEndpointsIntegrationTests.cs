using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProjectEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static CreateProjectActionDto CreateRequest(string name = "it-created", string? instructions = null)
    {
        return new CreateProjectActionDto
        {
            Name = name,
            Description = $"{name} description",
            Instructions = instructions
        };
    }

    private static UpdateProjectActionDto UpdateRequest(string name = "it-updated", string? instructions = null)
    {
        return new UpdateProjectActionDto
        {
            Name = name,
            Description = $"{name} description",
            Instructions = instructions
        };
    }

    #region Authorization
    [Fact]
    public async Task SearchProjects_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/projects", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task CreateProject_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("api/projects", CreateRequest(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SearchProjects_RegularUser_SeesOnlyTheirOwnProjects()
    {
        var mine = await _fixture.AddProjectAsync("it-mine", cancellationToken: TestContext.Current.CancellationToken);
        var theirs = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ProjectSummaryDto>>("api/projects", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Contains(page.Items, x => x.Id == mine);
        Assert.DoesNotContain(page.Items, x => x.Id == theirs);
    }

    [Fact]
    public async Task SearchProjects_TakeOfZero_IsClampedRatherThanReturningServerError()
    {
        // Straight off the query string: this used to divide by zero computing CurrentPage.
        await _fixture.AddProjectAsync("it-paging", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/projects?take=0&skip=-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponseDto<ProjectSummaryDto>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.InRange(page.PageSize, 1, 100);
    }

    [Fact]
    public async Task SearchProjects_Listing_DoesNotIncludeInstructions()
    {
        await _fixture.AddProjectAsync("it-instructed", instructions: "Answer in British English.", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var body = await client.GetStringAsync("api/projects", TestContext.Current.CancellationToken);

        Assert.Contains("it-instructed", body);
        Assert.DoesNotContain("British English", body);
    }

    [Fact]
    public async Task GetProject_ProjectOwnedByAnotherUser_ReturnsNotFound()
    {
        // 404 rather than 403 so the API cannot be used to confirm another user's project id exists.
        var projectId = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_ProjectOwnedByAnotherUser_ReturnsNotFoundAndLeavesItActive()
    {
        var projectId = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);

        var project = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(project);
        Assert.Null(project.DateDeactivated);
    }
    #endregion

    #region Create
    [Fact]
    public async Task CreateProject_ValidRequest_ReturnsCreatedWithLocation()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/projects", CreateRequest(instructions: "Answer in British English."), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ProjectDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal("it-created", created.Name);
        Assert.Equal("Answer in British English.", created.Instructions);
        Assert.Equal($"/api/projects/{created.Id}", response.Headers.Location?.OriginalString);

        var stored = await _fixture.FindProjectAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(TestUsers.RegularUserId, stored.UserId);
    }

    [Fact]
    public async Task CreateProject_EmptyName_ReturnsValidationProblem()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/projects", CreateRequest(string.Empty), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.True(problem.Errors.ContainsKey("Name"));
    }

    [Fact]
    public async Task CreateProject_NameAlreadyUsed_ReturnsValidationProblemRatherThanAnIndexViolation()
    {
        await _fixture.AddProjectAsync("it-duplicate", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/projects", CreateRequest("it-duplicate"), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["Name"], message => message.Contains("already exists"));
    }

    [Fact]
    public async Task CreateProject_NameUsedByAnotherUser_Succeeds()
    {
        await _fixture.AddProjectAsync("it-shared-name", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/projects", CreateRequest("it-shared-name"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    #endregion

    #region Update
    [Fact]
    public async Task UpdateProject_OwnedProject_ReturnsOkAndPersistsEveryField()
    {
        var projectId = await _fixture.AddProjectAsync("it-original", instructions: "Old.", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}", UpdateRequest(instructions: "New."), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal("it-updated", stored.Name);
        Assert.Equal("New.", stored.Instructions);
    }

    [Fact]
    public async Task UpdateProject_UnknownProject_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{Guid.NewGuid()}", UpdateRequest(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateProject_NameTakenByAnotherProject_ReturnsValidationProblem()
    {
        await _fixture.AddProjectAsync("it-taken", cancellationToken: TestContext.Current.CancellationToken);
        var projectId = await _fixture.AddProjectAsync("it-renaming", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}", UpdateRequest("it-taken"), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["Name"], message => message.Contains("already exists"));
    }

    [Fact]
    public async Task UpdateProject_KeepingItsOwnName_Succeeds()
    {
        var projectId = await _fixture.AddProjectAsync("it-stable", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}", UpdateRequest("it-stable", "Rewritten."), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    #endregion

    #region Favorites
    [Fact]
    public async Task SetProjectFavorite_OwnProject_MarksItAndNarrowsTheListing()
    {
        var favorite = await _fixture.AddProjectAsync("it-starred", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddProjectAsync("it-plain", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{favorite}/favorite",
            new SetProjectFavoriteActionDto { IsFavorite = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ProjectSummaryDto>>(
            "api/projects?isFavorite=true", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal(favorite, item.Id);
        Assert.True(item.IsFavorite);
    }

    [Fact]
    public async Task SetProjectFavorite_WhenCleared_LeavesDateModifiedWhereItWas()
    {
        // The flag is the whole write. A bump here would desynchronise every client that keeps its
        // own copy of the row on the strength of this promise.
        var projectId = await _fixture.AddProjectAsync("it-unstarring", isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);
        var before = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}/favorite",
            new SetProjectFavoriteActionDto { IsFavorite = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var stored = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.IsFavorite);
        Assert.Equal(before.DateModified, stored.DateModified);
    }

    [Fact]
    public async Task UpdateProject_RenamingAFavorite_LeavesTheStarInPlace()
    {
        // PUT api/projects/{id} is a full representation and carries no flag. If the update path
        // ever wrote one, every rename would silently un-favourite the project.
        var projectId = await _fixture.AddProjectAsync("it-starred-rename", isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}", UpdateRequest("it-renamed-starred"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.True(stored.IsFavorite);
    }

    [Fact]
    public async Task SetProjectFavorite_UnknownProject_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{Guid.NewGuid()}/favorite",
            new SetProjectFavoriteActionDto { IsFavorite = true },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetProjectFavorite_ProjectOwnedByAnotherUser_ReturnsNotFoundAndLeavesItUnstarred()
    {
        var projectId = await _fixture.AddProjectAsync(
            "it-someone-elses-star", userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/projects/{projectId}/favorite",
            new SetProjectFavoriteActionDto { IsFavorite = true },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);

        var stored = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.False(stored.IsFavorite);
    }

    [Fact]
    public async Task SearchProjects_FavoriteFilterOmitted_ReturnsFavoritesAndPlainAlike()
    {
        await _fixture.AddProjectAsync("it-both-starred", isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddProjectAsync("it-both-plain", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ProjectSummaryDto>>(
            "api/projects", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
    }
    #endregion

    #region Conversation membership
    [Fact]
    public async Task UpdateConversation_WithProjectId_MovesTheConversationIntoTheProject()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            "api/conversations",
            new { id = conversationId, name = "Moved", projectId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var conversation = await _fixture.FindConversationAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.NotNull(conversation);
        Assert.Equal(projectId, conversation.ProjectId);
    }

    [Fact]
    public async Task UpdateConversation_WithoutProjectId_RemovesTheConversationFromItsProject()
    {
        // The full-replace semantic is the most surprising part of this contract, so it is pinned
        // over the wire and not only at the service level.
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            "api/conversations",
            new { id = conversationId, name = "Detached" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var conversation = await _fixture.FindConversationAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.NotNull(conversation);
        Assert.Null(conversation.ProjectId);
    }

    [Fact]
    public async Task UpdateConversation_ProjectOwnedByAnotherUser_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            "api/conversations",
            new { id = conversationId, name = "Renamed", projectId },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);

        var conversation = await _fixture.FindConversationAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.NotNull(conversation);
        Assert.Null(conversation.ProjectId);
    }
    #endregion

    #region Delete
    [Fact]
    public async Task DeleteProject_ProjectWithConversations_KeepsThemAndClearsTheirProjectId()
    {
        // Deleting a project must never destroy the user's chat history.
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await _fixture.AddConversationAsync(projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var project = await _fixture.FindProjectAsync(projectId, TestContext.Current.CancellationToken);
        Assert.NotNull(project);
        Assert.NotNull(project.DateDeactivated);

        var conversation = await _fixture.FindConversationAsync(conversationId, TestContext.Current.CancellationToken);
        Assert.NotNull(conversation);
        Assert.Null(conversation.DateDeactivated);
        Assert.Null(conversation.ProjectId);
    }

    [Fact]
    public async Task DeleteProject_AlreadyDeleted_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync(deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_ThenRecreateWithTheSameName_Succeeds()
    {
        // The unique name index is filtered on DateDeactivated, so a deleted project must not
        // permanently reserve its name.
        var projectId = await _fixture.AddProjectAsync("it-recycled", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        await client.DeleteAsync($"api/projects/{projectId}", TestContext.Current.CancellationToken);
        var response = await client.PostAsJsonAsync(
            "api/projects", CreateRequest("it-recycled"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    #endregion

    #region Documents
    [Fact]
    public async Task GetProjectDocuments_NoUploads_ReturnsEmpty()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var documents = await client.GetFromJsonAsync<List<ProjectDocumentDto>>(
            $"api/projects/{projectId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task GetProjectDocuments_ProjectOwnedByAnotherUser_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync("it-theirs", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/projects/{projectId}/documents", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProjectDocument_UnknownDocument_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync(
            $"api/projects/{projectId}/documents/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion
}
