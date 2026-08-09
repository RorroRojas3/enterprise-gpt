using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Persistence;

/// <summary>
/// Covers document retrieval against a real SQL Server 2025, which is the only place it can run: the
/// unit-test fixture strips <c>SqlVector&lt;float&gt;</c> from the model, so <c>VECTOR_DISTANCE</c>, the
/// <c>UNION ALL</c> over both chunk tables and the neighbour read-back have no coverage anywhere else.
/// </summary>
/// <remarks>
/// The service is constructed here rather than resolved from the container so each test can set its own
/// retrieval options; the DbContext, the chunker and the schema are all the real ones.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DocumentRetrievalIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const int QueryAxis = 0;
    private const int OtherAxis = 7;

    private readonly IntegrationTestFixture _fixture = fixture;
    private readonly ScriptedEmbeddingGenerator _embeddingGenerator = new();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    #region Vector search

    [Fact]
    public async Task SearchAsync_RanksByCosineDistance()
    {
        var conversationId = await AddConversationAsync();

        // Deliberately non-adjacent: contiguous chunks merge into one passage, which is the point of
        // SearchAsync_MatchInTheMiddleOfADocument_ReturnsItWithItsNeighbours rather than of this test.
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "Distant material about unrelated subjects.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.45), 1),
            new SeedChunk(5, "Exactly what was asked about.", SeedChunk.UnitVector(QueryAxis), 2),
            new SeedChunk(10, "Related material about the same topic.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.80), 3)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "the question", NoNeighbours());

        Assert.Equal(3, result.ResultCount);
        Assert.Equal(
            ["Exactly what was asked about.", "Related material about the same topic.", "Distant material about unrelated subjects."],
            result.Results.Select(r => r.Text));

        // Reported as a similarity, not the cosine distance the database returns.
        Assert.Equal(1.0, result.Results[0].Score, 2);
        Assert.Equal(0.80, result.Results[1].Score, 2);
    }

    [Fact]
    public async Task SearchAsync_ChunkBeyondTheDistanceGate_IsNotReturned()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "Close enough to be evidence.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.90), 1),
            new SeedChunk(5, "Nowhere near the subject.", SeedChunk.UnitVector(OtherAxis), 2)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var options = NoNeighbours();
        options.MaxDistance = 0.5;

        var result = await SearchAsync(conversationId, "the question", options);

        // Nearest-neighbour search always returns something. Without the gate the second chunk comes
        // back looking exactly like grounded evidence.
        var passage = Assert.Single(result.Results);
        Assert.Equal("Close enough to be evidence.", passage.Text);
    }

    [Fact]
    public async Task SearchAsync_CitesTheDocumentAndPage()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "Refunds are issued within 30 days.", SeedChunk.UnitVector(QueryAxis), 12)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "refunds", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Equal("handbook.pdf p.12", passage.Citation);
        Assert.Equal(12, passage.Page);
    }

    [Fact]
    public async Task SearchAsync_FormatWithoutPages_CitesTheFileNameAlone()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "notes.txt",
        [
            new SeedChunk(0, "A plain text note.", SeedChunk.UnitVector(QueryAxis))
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "notes", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Equal("notes.txt", passage.Citation);
        Assert.Null(passage.Page);
    }

    #endregion

    #region Neighbour expansion and merging

    [Fact]
    public async Task SearchAsync_MatchInTheMiddleOfADocument_ReturnsItWithItsNeighbours()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "Before the match.", SeedChunk.UnitVector(OtherAxis), 1),
            new SeedChunk(1, "The match itself.", SeedChunk.UnitVector(QueryAxis), 1),
            new SeedChunk(2, "After the match.", SeedChunk.UnitVector(OtherAxis), 2),
            new SeedChunk(3, "Far too late.", SeedChunk.UnitVector(OtherAxis), 3)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "the question", DefaultOptions());

        // A 512-token chunk regularly stops mid-explanation, so the sentences either side come back too.
        var passage = Assert.Single(result.Results);
        Assert.Contains("Before the match.", passage.Text, StringComparison.Ordinal);
        Assert.Contains("The match itself.", passage.Text, StringComparison.Ordinal);
        Assert.Contains("After the match.", passage.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Far too late.", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_MatchOnTheFirstChunk_DoesNotAskForANegativeIndex()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "The very first chunk.", SeedChunk.UnitVector(QueryAxis), 1),
            new SeedChunk(1, "The second chunk.", SeedChunk.UnitVector(OtherAxis), 1)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "the question", DefaultOptions());

        var passage = Assert.Single(result.Results);
        Assert.StartsWith("The very first chunk.", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_OverlappingChunks_WriteTheSharedSeamOnce()
    {
        var conversationId = await AddConversationAsync();

        // Shaped the way the chunker shapes them: the head of each chunk is character-for-character the
        // tail of the last, because that is what the 128-token overlap produces.
        const string shared = "The escalation path runs through the duty manager on call.";
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, $"Incidents are triaged by severity. {shared}", SeedChunk.UnitVector(OtherAxis), 1),
            new SeedChunk(1, $"{shared} Escalation must happen within one hour.", SeedChunk.UnitVector(QueryAxis), 2)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "escalation", DefaultOptions());

        var passage = Assert.Single(result.Results);
        Assert.Equal(
            "Incidents are triaged by severity. The escalation path runs through the duty manager on call. Escalation must happen within one hour.",
            passage.Text);
    }

    [Fact]
    public async Task SearchAsync_MatchesFarApartInOneDocument_ComeBackAsSeparatePassages()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            new SeedChunk(0, "An early match.", SeedChunk.UnitVector(QueryAxis), 1),
            new SeedChunk(1, "Filler.", SeedChunk.UnitVector(OtherAxis), 1),
            new SeedChunk(2, "More filler.", SeedChunk.UnitVector(OtherAxis), 2),
            new SeedChunk(3, "More filler still.", SeedChunk.UnitVector(OtherAxis), 2),
            new SeedChunk(4, "A late match.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.95), 3)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "the question", DefaultOptions());

        Assert.Equal(2, result.ResultCount);
        Assert.Contains(result.Results, r => r.Text.Contains("An early match.", StringComparison.Ordinal));
        Assert.Contains(result.Results, r => r.Text.Contains("A late match.", StringComparison.Ordinal));
    }

    #endregion

    #region Keyword pass

    [Fact]
    public async Task SearchAsync_ExactTokenTheVectorPassMisses_IsStillFound()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "runbook.pdf",
        [
            new SeedChunk(0, "General notes about database maintenance windows.", SeedChunk.UnitVector(OtherAxis), 1),
            new SeedChunk(1, "Error ORA-01555 means the snapshot is too old; extend the undo retention.", SeedChunk.UnitVector(OtherAxis), 2)
        ], cancellationToken: TestContext.Current.CancellationToken);

        // Every chunk is orthogonal to the query, so nothing survives the distance gate. Embeddings are
        // weak on identifiers exactly like this one, which is why the keyword pass exists.
        var result = await SearchAsync(conversationId, "ORA-01555 snapshot too old", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Contains("ORA-01555", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_KeywordPassDisabled_MissesTheExactToken()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "runbook.pdf",
        [
            new SeedChunk(0, "Error ORA-01555 means the snapshot is too old.", SeedChunk.UnitVector(OtherAxis), 1)
        ], cancellationToken: TestContext.Current.CancellationToken);

        var options = NoNeighbours();
        options.EnableLexicalSearch = false;

        var result = await SearchAsync(conversationId, "ORA-01555 snapshot too old", options);

        // The counterpart of the previous test: it is the keyword pass doing the work, not the gate
        // happening to be generous.
        Assert.Equal(0, result.ResultCount);
    }

    [Fact]
    public async Task SearchAsync_QueryWithLikeWildcards_MatchesThemLiterally()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "pricing.pdf",
        [
            new SeedChunk(0, "Discounts of 100% apply to internal transfers.", SeedChunk.UnitVector(OtherAxis), 1),
            new SeedChunk(1, "Standard commercial terms with no discount.", SeedChunk.UnitVector(OtherAxis), 2)
        ], cancellationToken: TestContext.Current.CancellationToken);

        // Unescaped, the "%" would turn the pattern into a wildcard and match both chunks.
        var result = await SearchAsync(conversationId, "100% internal discounts", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Contains("100%", passage.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DifferentCasingAndAccents_StillMatches()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "policy.pdf",
        [
            new SeedChunk(0, "Le résumé du candidat est archivé pendant douze mois.", SeedChunk.UnitVector(OtherAxis), 1)
        ], cancellationToken: TestContext.Current.CancellationToken);

        // The explicit CI_AI collation is what makes this independent of how the database was created.
        var result = await SearchAsync(conversationId, "RESUME CANDIDAT archive", NoNeighbours());

        Assert.Equal(1, result.ResultCount);
    }

    #endregion

    #region Scope and isolation

    [Fact]
    public async Task SearchAsync_AnotherConversationsChunks_AreNeverReturned()
    {
        var mine = await AddConversationAsync();
        var theirs = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(mine, "mine.pdf",
            [new SeedChunk(0, "My own content.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.70), 1)],
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(theirs, "theirs.pdf",
            [new SeedChunk(0, "Someone else's content.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(mine, "content", NoNeighbours());

        // The closer chunk belongs to another conversation, so the ranking must not even see it.
        var passage = Assert.Single(result.Results);
        Assert.Equal("mine.pdf", passage.DocumentName);
    }

    [Fact]
    public async Task SearchAsync_SoftDeletedDocument_IsExcluded()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "deleted.pdf",
            [new SeedChunk(0, "Removed content.", SeedChunk.UnitVector(QueryAxis), 1)],
            deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(conversationId, "live.pdf",
            [new SeedChunk(0, "Current content.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.70), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "content", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Equal("live.pdf", passage.DocumentName);
    }

    [Fact]
    public async Task SearchAsync_SoftDeletedChunkUnderALiveDocument_IsExcluded()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
            [new SeedChunk(0, "Superseded content.", SeedChunk.UnitVector(QueryAxis), 1)],
            chunksDeactivated: true, cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "content", NoNeighbours());

        // Soft delete has no query filter on this model, so every level is filtered by hand and each
        // one needs its own proof.
        Assert.Equal(0, result.ResultCount);
    }

    [Fact]
    public async Task SearchAsync_ConversationInAProject_SearchesBothCorpora()
    {
        var projectId = await _fixture.AddProjectAsync(
            $"it-retrieval-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await AddConversationAsync(projectId);
        await _fixture.AddConversationDocumentAsync(conversationId, "conversation.pdf",
            [new SeedChunk(0, "Uploaded into the conversation.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.80), 1)],
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddProjectDocumentAsync(projectId, "project.pdf",
            [new SeedChunk(0, "Uploaded into the project.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "uploaded", NoNeighbours());

        Assert.Equal(2, result.ResultCount);
        Assert.Equal(["project.pdf", "conversation.pdf"], result.Results.Select(r => r.DocumentName));
    }

    [Fact]
    public async Task SearchAsync_StandaloneConversation_NeverReachesProjectDocuments()
    {
        var projectId = await _fixture.AddProjectAsync(
            $"it-retrieval-{Guid.NewGuid():N}", cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "conversation.pdf",
            [new SeedChunk(0, "Uploaded into the conversation.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.70), 1)],
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddProjectDocumentAsync(projectId, "project.pdf",
            [new SeedChunk(0, "Uploaded into the project.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "uploaded", NoNeighbours());

        var passage = Assert.Single(result.Results);
        Assert.Equal("conversation.pdf", passage.DocumentName);
    }

    [Fact]
    public async Task GetScopeAsync_DeactivatedProject_DropsItsDocuments()
    {
        var projectId = await _fixture.AddProjectAsync(
            $"it-retrieval-{Guid.NewGuid():N}", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        var conversationId = await AddConversationAsync(projectId);
        await _fixture.AddProjectDocumentAsync(projectId, "project.pdf",
            [new SeedChunk(0, "Uploaded into the project.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        using var scope = _fixture.Factory.Services.CreateScope();
        var service = CreateService(scope, DefaultOptions());

        var documentScope = await service.GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.Null(documentScope.ProjectId);
        Assert.False(documentScope.HasDocuments);
    }

    [Fact]
    public async Task SearchAsync_RestrictedToOneDocument_SearchesOnlyThatOne()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
            [new SeedChunk(0, "Handbook content.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(conversationId, "policy.docx",
            [new SeedChunk(0, "Policy content.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "content", NoNeighbours(), documentName: "policy.docx");

        var passage = Assert.Single(result.Results);
        Assert.Equal("policy.docx", passage.DocumentName);
    }

    [Fact]
    public async Task SearchAsync_UnknownDocumentName_SearchesEverythingAndSaysSo()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
            [new SeedChunk(0, "Handbook content.", SeedChunk.UnitVector(QueryAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "content", NoNeighbours(), documentName: "missing.pdf");

        // Returning nothing for a near-miss on a file name reads to the model as "the documents do not
        // say", which is a worse failure than a wider search with an explanation attached.
        Assert.Equal(1, result.ResultCount);
        Assert.NotNull(result.Note);
        Assert.Contains("handbook.pdf", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_NothingMatches_ListsWhatCouldHaveBeenSearched()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
            [new SeedChunk(0, "Entirely unrelated.", SeedChunk.UnitVector(OtherAxis), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await SearchAsync(conversationId, "zzzz", NoNeighbours());

        Assert.Equal(0, result.ResultCount);
        Assert.Equal(["handbook.pdf"], result.AvailableDocuments);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public async Task SearchAsync_OneDocumentDominating_IsCappedSoOthersSurvive()
    {
        var conversationId = await AddConversationAsync();
        await _fixture.AddConversationDocumentAsync(conversationId, "loud.pdf",
        [
            new SeedChunk(0, "Loud one.", SeedChunk.UnitVector(QueryAxis), 1),
            new SeedChunk(5, "Loud two.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.99), 2),
            new SeedChunk(10, "Loud three.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.98), 3)
        ], cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddConversationDocumentAsync(conversationId, "quiet.pdf",
            [new SeedChunk(0, "Quiet one.", SeedChunk.Blend(QueryAxis, OtherAxis, 0.90), 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        var options = NoNeighbours();
        options.MaxPassagesPerDocument = 2;

        var result = await SearchAsync(conversationId, "the question", options);

        Assert.Equal(3, result.ResultCount);
        Assert.Contains(result.Results, r => r.DocumentName == "quiet.pdf");
        Assert.Equal(2, result.Results.Count(r => r.DocumentName == "loud.pdf"));
    }

    #endregion

    #region Engine requirements

    [Fact]
    public async Task Retrieval_NeedsNoVectorIndexAndNoPreviewFeatures()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        var connection = (SqlConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM sys.vector_indexes) AS VectorIndexCount,
                    (SELECT CAST(value AS int) FROM sys.database_scoped_configurations WHERE [name] = 'PREVIEW_FEATURES') AS PreviewFeatures;
                """;

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

            // Everything above ran on this database. An approximate index would make its table read-only
            // on Azure SQL and stop document upload, so retrieval has to work without one.
            Assert.Equal(0, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    #endregion

    #region Helpers

    private async Task<Guid> AddConversationAsync(Guid? projectId = null) =>
        await _fixture.AddConversationAsync(
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

    private static DocumentRetrievalOptions DefaultOptions() => new();

    /// <summary>
    /// Options with expansion switched off, so a test asserting on ranking sees one passage per matching
    /// chunk instead of runs merged by adjacency.
    /// </summary>
    private static DocumentRetrievalOptions NoNeighbours() => new() { NeighborWindow = 0 };

    private DocumentRetrievalService CreateService(IServiceScope scope, DocumentRetrievalOptions options) => new(
        NullLogger<DocumentRetrievalService>.Instance,
        _embeddingGenerator,
        scope.ServiceProvider.GetRequiredService<ITextChunker>(),
        Options.Create(options),
        scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>());

    /// <summary>
    /// Runs a search with the query embedded as the unit vector on <see cref="QueryAxis"/>, so every
    /// seeded chunk's distance from it is exactly what <see cref="SeedChunk.Blend"/> was asked for.
    /// </summary>
    private async Task<DocumentSearchResult> SearchAsync(
        Guid conversationId, string query, DocumentRetrievalOptions options, string? documentName = null)
    {
        _embeddingGenerator.Vector = SeedChunk.UnitVector(QueryAxis);

        using var scope = _fixture.Factory.Services.CreateScope();
        var service = CreateService(scope, options);
        var documentScope = await service.GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        return await service.SearchAsync(documentScope, query, documentName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Returns a vector the test chooses, so the distances SQL Server computes are exact and the
    /// assertions can name them.
    /// </summary>
    private sealed class ScriptedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public float[] Vector { get; set; } = SeedChunk.UnitVector(0);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(Vector))));

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
