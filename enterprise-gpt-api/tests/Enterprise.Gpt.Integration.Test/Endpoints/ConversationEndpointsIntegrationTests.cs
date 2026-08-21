using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Chat;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConversationEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
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

    private static HttpRequestMessage BulkDeleteRequest(params Guid[] conversationIds)
    {
        // HttpClient has no DeleteAsJsonAsync: the body has to be attached to the request by hand,
        // which is also the shape the Angular client sends.
        return new HttpRequestMessage(HttpMethod.Delete, "api/conversations/bulk")
        {
            Content = JsonContent.Create(new DeactivateConversationsBulkActionDto { ConversationIds = [.. conversationIds] })
        };
    }

    #region Ordering
    [Fact]
    public async Task SearchConversations_SortedByNameAscending_OrdersAlphabetically()
    {
        await _fixture.AddConversationAsync(name: "it-order-zulu", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(name: "it-order-alpha", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(name: "it-order-mike", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            "api/conversations/search?name=it-order-&sort=name&dir=asc", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(
            ["it-order-alpha", "it-order-mike", "it-order-zulu"],
            page.Items.Select(x => x.Name));
    }

    [Fact]
    public async Task SearchConversations_SortedByNameDescending_ReversesTheOrder()
    {
        await _fixture.AddConversationAsync(name: "it-rorder-zulu", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(name: "it-rorder-alpha", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(name: "it-rorder-mike", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            "api/conversations/search?name=it-rorder-&sort=name&dir=desc", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(
            ["it-rorder-zulu", "it-rorder-mike", "it-rorder-alpha"],
            page.Items.Select(x => x.Name));
    }

    [Fact]
    public async Task SearchConversations_UnrecognisedSortField_ReturnsAValidationProblemNamingTheParameter()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            "api/conversations/search?sort=updated", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Equal("/problems/validation-error", problem.Type);
        Assert.Single(Assert.Contains("sort", problem.Errors));
    }
    #endregion

    #region Authorization
    [Fact]
    public async Task SearchConversations_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync("api/conversations/search", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Fact]
    public async Task CreateConversation_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "api/conversations", new CreateConversationActionDto(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SearchConversations_RegularUser_SeesOnlyTheirOwnConversations()
    {
        var mine = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var theirs = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            "api/conversations/search", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Contains(page.Items, x => x.Id == mine);
        Assert.DoesNotContain(page.Items, x => x.Id == theirs);
    }
    #endregion

    #region Project filter
    [Fact]
    public async Task SearchConversations_FilteredByProject_ReturnsOnlyThatProjectsConversations()
    {
        var projectId = await _fixture.AddProjectAsync(
            $"Filter {Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var inProject = await _fixture.AddConversationAsync(
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        // Active only: the project filter narrows the base predicate rather than replacing it.
        await _fixture.AddConversationAsync(
            projectId: projectId, deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            $"api/conversations/search?projectId={projectId}", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(inProject, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task SearchConversations_ProjectBelongingToAnotherUser_ReturnsNotFound()
    {
        var projectId = await _fixture.AddProjectAsync(
            $"Foreign {Guid.NewGuid():N}", TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/search?projectId={projectId}", TestContext.Current.CancellationToken);

        // 404, not an empty page: the route must not double as a probe for project ids.
        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion

    #region Reads
    [Fact]
    public async Task GetConversation_OwnConversation_ReturnsIt()
    {
        var id = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var conversation = await client.GetFromJsonAsync<ConversationDetailDto>(
            $"api/conversations/{id}", TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(id, conversation.Id);
    }

    [Fact]
    public async Task GetConversation_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    // Reported as not-found rather than forbidden so the route cannot be used to probe for ids.
    [Fact]
    public async Task GetConversation_AnotherUsersConversation_ReturnsNotFound()
    {
        var theirs = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{theirs}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The transcript lives in Cosmos and the row lives in SQL, and the transcript cutover destroyed
    /// every transcript while leaving every row. A conversation in that state has to open with an
    /// empty transcript: it is visible in the sidebar, so answering 404 would strand a row the user
    /// can see. A foreign or deactivated id still 404s — that is the case below.
    /// </summary>
    [Fact]
    public async Task GetConversationMessages_NoTranscriptDocument_ReturnsAnEmptyTranscript()
    {
        var id = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{id}/messages", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var transcript = await response.Content.ReadFromJsonAsync<ChatConversationDto>(TestContext.Current.CancellationToken);

        Assert.NotNull(transcript);
        Assert.Equal(id, transcript.Id);
        Assert.Empty(transcript.Messages);
    }

    [Fact]
    public async Task GetConversationMessages_AnotherUsersConversation_ReturnsNotFound()
    {
        var theirs = await _fixture.AddConversationAsync(
            TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{theirs}/messages", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The resume shape: the model comes off the conversation row, the MCP servers off the newest
    /// chat turn in the usage audit. Both tables are exercised for real here — they exist only in
    /// the model, so a mapping that SQLite tolerates but SQL Server rejects surfaces at this level.
    /// </summary>
    [Fact]
    public async Task GetConversation_AfterSeveralTurns_ReturnsTheNewestTurnsModelAndMcpServers()
    {
        var modelId = await _fixture.AddModelAsync($"it-usage-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var id = await _fixture.AddConversationAsync(modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        // Granted, because the read only hands back servers the next turn would actually accept.
        var (olderServer, olderPermission) = await _fixture.AddMcpServerAsync($"it-usage-old-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var (newerServer, newerPermission) = await _fixture.AddMcpServerAsync($"it-usage-new-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, olderPermission!.Value, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, newerPermission!.Value, cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        await _fixture.AddConversationUsageAsync(
            id, modelId, now.AddMinutes(-10), mcpServerIds: [olderServer], cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            id, modelId, now, mcpServerIds: [newerServer], cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var conversation = await client.GetFromJsonAsync<ConversationDetailDto>(
            $"api/conversations/{id}", TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(modelId, conversation.ModelId);
        Assert.Equal(newerServer, Assert.Single(conversation.McpServerIds));
    }

    // Naming calls never carry tools, so counting them as the newest turn would blank the selection
    // a conversation was actually last run with.
    [Fact]
    public async Task GetConversation_WhenTheNewestUsageIsANamingCall_StillReturnsTheLastChatTurnsMcpServers()
    {
        var modelId = await _fixture.AddModelAsync($"it-usage-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var id = await _fixture.AddConversationAsync(modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        var (server, permission) = await _fixture.AddMcpServerAsync($"it-usage-chat-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, permission!.Value, cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        await _fixture.AddConversationUsageAsync(
            id, modelId, now.AddMinutes(-1), mcpServerIds: [server], cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            id, modelId, now, kind: ConversationUsageKinds.Naming, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var conversation = await client.GetFromJsonAsync<ConversationDetailDto>(
            $"api/conversations/{id}", TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(server, Assert.Single(conversation.McpServerIds));
    }

    // Handing back a server the caller can no longer use would have the client replay it into the
    // next turn and take a 403 it cannot act on.
    [Fact]
    public async Task GetConversation_WhenTheGrantForTheLastTurnsServerWasRevoked_OmitsIt()
    {
        var modelId = await _fixture.AddModelAsync($"it-usage-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var id = await _fixture.AddConversationAsync(modelId: modelId, cancellationToken: TestContext.Current.CancellationToken);
        var (granted, grantedPermission) = await _fixture.AddMcpServerAsync($"it-usage-kept-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var (revoked, _) = await _fixture.AddMcpServerAsync($"it-usage-revoked-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, grantedPermission!.Value, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationUsageAsync(
            id, modelId, DateTimeOffset.UtcNow, mcpServerIds: [granted, revoked], cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var conversation = await client.GetFromJsonAsync<ConversationDetailDto>(
            $"api/conversations/{id}", TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(granted, Assert.Single(conversation.McpServerIds));
    }

    [Fact]
    public async Task SearchConversations_TakeOfZero_IsClampedRatherThanReturningServerError()
    {
        // Straight off the query string: this used to divide by zero computing CurrentPage.
        await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync("api/conversations/search?take=0&skip=-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponseDto<ConversationDto>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.InRange(page.PageSize, 1, 100);
    }
    #endregion

    #region Export
    [Fact]
    public async Task ExportConversation_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.GetAsync(
            $"api/conversations/{Guid.NewGuid()}/export", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The rejection has to name the parameter and the whole supported set, because the client has no
    /// other way to learn which formats this API accepts.
    /// </summary>
    [Fact]
    public async Task ExportConversation_UnsupportedFormat_ReturnsValidationProblemNamingTheParameter()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/{conversationId}/export?format=rtf", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("/problems/validation-error", problem.Type);
        var messages = Assert.Contains("format", problem.Errors);
        var message = Assert.Single(messages);

        foreach (var supported in new[] { "html", "json", "md", "docx", "pdf" })
        {
            Assert.Contains(supported, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The format check runs before ownership, so an unsupported format on someone else's
    /// conversation must still answer 400 rather than confirming the row by 404-ing.
    /// </summary>
    [Fact]
    public async Task ExportConversation_UnsupportedFormatOnAnotherUsersConversation_StillReturnsBadRequest()
    {
        var conversationId = await _fixture.AddConversationAsync(
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/{conversationId}/export?format=rtf", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExportConversation_AnotherUsersConversation_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/{conversationId}/export?format=md", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("/problems/resource-not-found", problem.Type);
    }

    [Fact]
    public async Task ExportConversation_DeactivatedConversation_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/{conversationId}/export?format=md", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExportConversation_UnknownConversation_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync(
            $"api/conversations/{Guid.NewGuid()}/export?format=md", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion

    #region Document listing
    [Fact]
    public async Task GetConversationDocuments_OwnedConversationWithDocuments_ReturnsThem()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "handbook.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            cancellationToken: TestContext.Current.CancellationToken);
        var otherConversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            otherConversationId, "other.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(1))],
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var documents = await client.GetFromJsonAsync<List<ConversationDocumentDto>>(
            $"api/conversations/{conversationId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        var document = Assert.Single(documents);
        Assert.Equal(documentId, document.Id);
        Assert.Equal(documentId, document.DocumentId);
        Assert.Equal(conversationId, document.ConversationId);
        Assert.Equal("handbook.pdf", document.Name);
        Assert.Equal(".pdf", document.Extension);
        Assert.Equal("application/octet-stream", document.MimeType);
        Assert.Equal(1024, document.Size);
        Assert.NotEqual(default, document.DateCreated);
    }

    [Fact]
    public async Task GetConversationDocuments_NoDocuments_ReturnsEmpty()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var documents = await client.GetFromJsonAsync<List<ConversationDocumentDto>>(
            $"api/conversations/{conversationId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        Assert.Empty(documents);
    }

    [Fact]
    public async Task GetConversationDocuments_DeactivatedDocument_IsExcluded()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(
            conversationId, "gone.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(0))],
            deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        var keptId = await _fixture.AddConversationDocumentAsync(
            conversationId, "kept.pdf", [new SeedChunk(0, "chunk", SeedChunk.UnitVector(1))],
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var documents = await client.GetFromJsonAsync<List<ConversationDocumentDto>>(
            $"api/conversations/{conversationId}/documents", TestContext.Current.CancellationToken);

        Assert.NotNull(documents);
        Assert.Equal(keptId, Assert.Single(documents).Id);
    }

    // Reported as not-found rather than forbidden so the route cannot be used to probe for ids.
    [Fact]
    public async Task GetConversationDocuments_AnotherUsersConversation_ReturnsNotFound()
    {
        var theirs = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{theirs}/documents", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConversationDocuments_DeactivatedConversation_ReturnsNotFound()
    {
        var id = await _fixture.AddConversationAsync(deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{id}/documents", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConversationDocuments_UnknownConversation_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.GetAsync($"api/conversations/{Guid.NewGuid()}/documents", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }
    #endregion

    #region Writes
    [Fact]
    public async Task CreateConversation_Standalone_ReturnsCreatedWithLocation()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/conversations", new CreateConversationActionDto(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ConversationDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal($"/api/conversations/{created.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task CreateConversation_UnknownProject_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            "api/conversations",
            new CreateConversationActionDto { ProjectId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    // The id travels in the body on this route rather than the URL, unlike the sibling modules.
    [Fact]
    public async Task UpdateConversation_OwnConversation_RenamesIt()
    {
        var id = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            "api/conversations",
            new UpdateConversationActionDto { Id = id, Name = "it-renamed" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ConversationDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("it-renamed", updated.Name);
    }

    [Fact]
    public async Task UpdateConversation_MissingName_ReturnsValidationProblem()
    {
        var id = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            "api/conversations",
            new UpdateConversationActionDto { Id = id, Name = string.Empty },
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains(nameof(UpdateConversationActionDto.Name), problem.Errors.Keys);
    }

    [Fact]
    public async Task SetConversationFavorite_OwnConversation_MarksItAndNarrowsTheSearch()
    {
        var favorite = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/conversations/{favorite}/favorite",
            new SetConversationFavoriteActionDto { IsFavorite = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            "api/conversations/search?isFavorite=true", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(favorite, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task SetConversationFavorite_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PutAsJsonAsync(
            $"api/conversations/{Guid.NewGuid()}/favorite",
            new SetConversationFavoriteActionDto { IsFavorite = true },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivateConversation_OwnConversation_RemovesItFromReads()
    {
        var id = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/conversations/{id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var reread = await client.GetAsync($"api/conversations/{id}", TestContext.Current.CancellationToken);
        await ProblemAssert.ReadAsync(reread, HttpStatusCode.NotFound);
    }

    // Deliberately not a 404: the service treats an already-gone conversation as done, so the route
    // is idempotent and declares no not-found response.
    [Fact]
    public async Task DeactivateConversation_UnknownId_ReturnsNoContent()
    {
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync($"api/conversations/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The route minimal APIs are most likely to regress on: DELETE does not infer a body parameter,
    // so this fails with a 400 unless the handler is explicitly annotated [FromBody].
    [Fact]
    public async Task DeactivateConversationsBulk_OwnConversations_DeactivatesEveryOne()
    {
        var first = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        using var request = BulkDeleteRequest(first, second);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var page = await client.GetFromJsonAsync<PaginatedResponseDto<ConversationDto>>(
            "api/conversations/search", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task DeactivateConversationsBulk_EmptyList_ReturnsValidationProblem()
    {
        using var client = _fixture.Factory.CreateUserClient();

        using var request = BulkDeleteRequest();
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains(nameof(DeactivateConversationsBulkActionDto.ConversationIds), problem.Errors.Keys);
    }

    [Fact]
    public async Task DeactivateConversationsBulk_AnotherUsersConversation_LeavesItAlone()
    {
        var theirs = await _fixture.AddConversationAsync(TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        using var request = BulkDeleteRequest(theirs);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var adminClient = _fixture.Factory.CreateAdminClient();
        var stillThere = await adminClient.GetAsync($"api/conversations/{theirs}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }
    #endregion

    #region Message feedback
    /// <summary>
    /// The route's happy path end to end: the transcript document is patched and the reporting row
    /// lands beside it. Reading the rating back through <c>GET .../messages</c> is not asserted
    /// here on purpose — that read goes through a transcript query, which the in-memory store
    /// refuses to implement; the emulator-backed persistence tests own that half.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_OwnAssistantMessage_RecordsTheRating()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Equal(MessageFeedbackRatings.Up, row.Rating);
        Assert.Equal(TestUsers.RegularUserId, row.UserId);
    }

    /// <summary>
    /// Re-rating replaces. The unique index on the projection is what makes a second row impossible,
    /// so a regression here fails as a 500 rather than as a silent duplicate.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_RatedAgainWithTheOppositeValue_ReplacesRatherThanDuplicates()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var route = $"api/conversations/{conversationId}/messages/{messageId}/feedback";

        await client.PostAsJsonAsync(
            route, new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(
            route, new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Down },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var row = await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken);
        Assert.Equal(MessageFeedbackRatings.Down, row?.Rating);
    }

    /// <summary>
    /// Withdrawing leaves the row and nulls the verdict: the row is the record that this message was
    /// considered, which reporting wants to keep.
    /// </summary>
    /// <remarks>
    /// The document is seeded already rated rather than rated through the API first, because the
    /// in-memory transcript store records patch operations without applying them — so a rating made
    /// here would not be on the document the withdrawal reads, and the withdrawal would correctly
    /// conclude there was nothing to withdraw.
    /// </remarks>
    [Fact]
    public async Task SetMessageFeedback_NullRatingOnARatedMessage_WithdrawsItAndKeepsTheRow()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, rating: MessageFeedbackRatings.Up,
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();
        var route = $"api/conversations/{conversationId}/messages/{messageId}/feedback";

        await client.PostAsJsonAsync(
            route, new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);
        var withdrawal = await client.PostAsJsonAsync(
            route, new SetMessageFeedbackActionDto { Rating = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, withdrawal.StatusCode);
        var row = await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken);
        Assert.NotNull(row);
        Assert.Null(row.Rating);
    }

    [Fact]
    public async Task SetMessageFeedback_UserMessage_ReturnsValidationProblemNamingTheRouteParameter()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, ChatRoles.User, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("/problems/validation-error", problem.Type);
        Assert.Contains("messageId", problem.Errors);
    }

    [Fact]
    public async Task SetMessageFeedback_UnknownMessage_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{Guid.NewGuid()}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Someone else's conversation is a 404 rather than a 403, consistently with the rest of the
    /// group, so the route cannot be used to confirm that a conversation or message exists.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_AnotherUsersConversation_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, userId: TestUsers.AdminUserId,
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Null(await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetMessageFeedback_Anonymous_ReturnsUnauthorized()
    {
        using var client = _fixture.Factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{Guid.NewGuid()}/messages/{Guid.NewGuid()}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A raw body rather than the DTO, because serializing the DTO would only prove the client and
    /// the server agree with themselves. This pins acceptance of the documented string form — not
    /// its casing, which System.Text.Json reads case-insensitively whatever this sends. The casing
    /// that is a contract is the one on the way <em>out</em>, and
    /// <c>ConversationMessageDtoTests</c> pins that.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_RatingAsAString_IsAccepted()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new StringContent("""{"rating":"Down"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken);
        Assert.Equal(MessageFeedbackRatings.Down, row?.Rating);
    }

    /// <summary>
    /// A rating naming nothing the enum defines fails during binding, before any validator could
    /// see it, so this 400 carries no errors dictionary.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_UnparseableRating_ReturnsABindingFailure()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new StringContent("""{"rating":"sideways"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Null(await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The hole the validator closes, end to end: the string-enum converter accepts integers and
    /// does not check them against the enum, so an integer nobody defined binds cleanly and would
    /// otherwise be stored — and would then come back out of the transcript as a number, breaking
    /// the string wire format for every client reading that field.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_RatingAsAnUndefinedInteger_ReturnsValidationProblemAndStoresNothing()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new StringContent("""{"rating":7}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("/problems/validation-error", problem.Type);
        Assert.Contains(nameof(SetMessageFeedbackActionDto.Rating), problem.Errors);
        Assert.Null(await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The header document lives in the same partition under the conversation's own id, so the one
    /// id a caller can supply that resolves to a real document without being a message has to be
    /// answered as the 404 it is.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_ConversationIdPassedAsTheMessageId_ReturnsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{conversationId}/feedback",
            new SetMessageFeedbackActionDto { Rating = MessageFeedbackRatings.Up },
            TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Withdrawing a rating nobody recorded touches neither store, so no client can file a row per
    /// assistant message by posting null ratings across a transcript.
    /// </summary>
    [Fact]
    public async Task SetMessageFeedback_NullRatingOnAnUnratedMessage_FilesNoProjectionRow()
    {
        var conversationId = await _fixture.AddConversationAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var messageId = await _fixture.AddTranscriptMessageAsync(
            conversationId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.PostAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{messageId}/feedback",
            new SetMessageFeedbackActionDto { Rating = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await _fixture.ReadMessageFeedbackAsync(
            conversationId, messageId, TestContext.Current.CancellationToken));
    }
    #endregion
}
