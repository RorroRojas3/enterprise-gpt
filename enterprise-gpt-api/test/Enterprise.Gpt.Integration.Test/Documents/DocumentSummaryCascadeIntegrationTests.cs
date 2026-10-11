using System.Net;
using System.Net.Http.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Documents;

/// <summary>
/// A summary must never outlive its document, since it is a readable account of everything the document said.
/// </summary>
/// <remarks>
/// Runs through the real routes because the cascades are <c>ExecuteUpdateAsync</c> statements the change
/// tracker never sees.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DocumentSummaryCascadeIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly SeedChunk[] Chunks =
        [new(0, "The refund window is thirty days.", SeedChunk.UnitVector(0))];

    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task DeactivateConversation_DocumentHasASummary_DeactivatesItAlongside()
    {
        var (conversationId, _, summaryId) = await AddConversationDocumentWithSummaryAsync();
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync(
            $"api/conversations/{conversationId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var summary = await _fixture.FindDocumentSummaryAsync(
            summaryId, DocumentSources.Conversation, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.NotNull(summary.DateDeactivated);
    }

    [Fact]
    public async Task DeactivateConversationsBulk_DocumentsHaveSummaries_DeactivatesEveryOne()
    {
        var first = await AddConversationDocumentWithSummaryAsync();
        var second = await AddConversationDocumentWithSummaryAsync();
        using var client = _fixture.Factory.CreateUserClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "api/conversations/bulk")
        {
            Content = JsonContent.Create(new { conversationIds = new[] { first.ConversationId, second.ConversationId } })
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        foreach (var summaryId in new[] { first.SummaryId, second.SummaryId })
        {
            var summary = await _fixture.FindDocumentSummaryAsync(
                summaryId, DocumentSources.Conversation, TestContext.Current.CancellationToken);

            Assert.NotNull(summary);
            Assert.NotNull(summary.DateDeactivated);
        }
    }

    [Fact]
    public async Task DeactivateProject_DocumentHasASummary_DeactivatesItAlongside()
    {
        var (projectId, _, summaryId) = await AddProjectDocumentWithSummaryAsync();
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync(
            $"api/projects/{projectId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var summary = await _fixture.FindDocumentSummaryAsync(
            summaryId, DocumentSources.Project, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.NotNull(summary.DateDeactivated);
    }

    [Fact]
    public async Task DeactivateProjectDocument_DocumentHasASummary_DeactivatesItAlongside()
    {
        var (projectId, documentId, summaryId) = await AddProjectDocumentWithSummaryAsync();
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync(
            $"api/projects/{projectId}/documents/{documentId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var summary = await _fixture.FindDocumentSummaryAsync(
            summaryId, DocumentSources.Project, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.NotNull(summary.DateDeactivated);
    }

    [Fact]
    public async Task DeactivateConversationDocument_DocumentHasASummary_DeactivatesItAlongside()
    {
        var (conversationId, documentId, summaryId) = await AddConversationDocumentWithSummaryAsync();
        using var client = _fixture.Factory.CreateUserClient();

        var response = await client.DeleteAsync(
            $"api/documents/conversations/{conversationId}/{documentId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var summary = await _fixture.FindDocumentSummaryAsync(
            summaryId, DocumentSources.Conversation, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.NotNull(summary.DateDeactivated);
    }

    private async Task<(Guid ConversationId, Guid DocumentId, Guid SummaryId)> AddConversationDocumentWithSummaryAsync()
    {
        var token = TestContext.Current.CancellationToken;

        var modelId = await _fixture.AddModelAsync($"it-summary-{Guid.NewGuid():N}", cancellationToken: token);
        var conversationId = await _fixture.AddConversationAsync(modelId: modelId, cancellationToken: token);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, $"handbook-{Guid.NewGuid():N}.pdf", Chunks, cancellationToken: token);

        var summaryId = await _fixture.AddDocumentSummaryAsync(
            documentId, DocumentSources.Conversation, modelId, token);

        return (conversationId, documentId, summaryId);
    }

    private async Task<(Guid ProjectId, Guid DocumentId, Guid SummaryId)> AddProjectDocumentWithSummaryAsync()
    {
        var token = TestContext.Current.CancellationToken;

        var modelId = await _fixture.AddModelAsync($"it-summary-{Guid.NewGuid():N}", cancellationToken: token);
        var projectId = await _fixture.AddProjectAsync(cancellationToken: token);
        var documentId = await _fixture.AddProjectDocumentAsync(
            projectId, $"shared-{Guid.NewGuid():N}.pdf", Chunks, cancellationToken: token);

        var summaryId = await _fixture.AddDocumentSummaryAsync(
            documentId, DocumentSources.Project, modelId, token);

        return (projectId, documentId, summaryId);
    }
}
