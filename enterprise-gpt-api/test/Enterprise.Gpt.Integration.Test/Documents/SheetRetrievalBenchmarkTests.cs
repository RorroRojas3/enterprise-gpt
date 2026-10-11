using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Extraction;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Documents;

/// <summary>
/// Measures how the shipped retrieval defaults behave over a corpus mixing prose with the
/// header-repeating row windows a spreadsheet is chunked into.
/// </summary>
/// <remarks>
/// The corpus text is real, but the distances are assigned rather than measured, so these numbers
/// describe the gate, the fusion and the caps over a row-window corpus — not embedding geometry. See
/// <c>docs/documents/retrieval.md</c>.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SheetRetrievalBenchmarkTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const int QueryAxis = 0;
    private const int OtherAxis = 7;

    /// <summary>Similarity of a chunk that answers the query outright.</summary>
    private const double Relevant = 0.85;

    /// <summary>Similarity of a chunk that is on topic without answering it.</summary>
    private const double Marginal = 0.45;

    /// <summary>Similarity of a chunk about something else entirely.</summary>
    private const double Irrelevant = 0.20;

    private readonly IntegrationTestFixture _fixture = fixture;
    private readonly ScriptedEmbeddingGenerator _embeddingGenerator = new() { Vector = SeedChunk.UnitVector(QueryAxis) };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The sweep behind the shipped threshold, over a corpus whose spreadsheet half repeats its header
    /// in almost every chunk: 0.40 loses the on-topic prose entirely, 0.85 admits prose about something
    /// else, and 0.62 is the one that takes the first without the second.
    /// </summary>
    [Theory]
    [InlineData(0.40, false, false)]
    [InlineData(0.62, true, false)]
    [InlineData(0.85, true, true)]
    public async Task SearchAsync_MixedCorpus_TheDistanceGateDecidesWhichProseSurvives(
        double maxDistance, bool expectsOnTopicProse, bool expectsOffTopicProse)
    {
        var conversationId = await SeedMixedCorpusAsync();

        var result = await SearchAsync(conversationId, "revenue", new DocumentRetrievalOptions
        {
            MaxDistance = maxDistance,
            NeighborWindow = 0,
            MaxResults = 20,
            EnableLexicalSearch = false
        });

        Assert.Contains(result.Results, r => r.DocumentName == "budget.xlsx");
        Assert.Equal(expectsOnTopicProse, result.Results.Any(r => r.Text.Contains("Revenue is recognised", StringComparison.Ordinal)));
        Assert.Equal(expectsOffTopicProse, result.Results.Any(r => r.Text.Contains("Parking permits", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Seeds a workbook chunked by the real extractor and chunker, plus two prose documents, so the
    /// search runs over the chunk shapes ingestion actually produces.
    /// </summary>
    private async Task<Guid> SeedMixedCorpusAsync()
    {
        var conversationId = await AddConversationAsync();
        var windows = await ChunkWorkbookAsync();

        await _fixture.AddConversationDocumentAsync(conversationId, "budget.xlsx",
            [.. windows.Select((window, i) => new SeedChunk(
                i, window.Text, SeedChunk.Blend(QueryAxis, OtherAxis, Relevant), window.SourceNumber))],
            cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddConversationDocumentAsync(conversationId, "handbook.pdf",
        [
            Chunk(0, "Revenue is recognised when the invoice is issued.", Marginal),
            Chunk(2, "Expense claims are approved by the duty manager.", Irrelevant)
        ], cancellationToken: TestContext.Current.CancellationToken);

        await _fixture.AddConversationDocumentAsync(conversationId, "policy.pdf",
        [
            Chunk(0, "Revenue targets are reviewed each quarter.", Marginal),
            Chunk(2, "Parking permits are issued annually.", Irrelevant)
        ], cancellationToken: TestContext.Current.CancellationToken);

        return conversationId;
    }

    /// <summary>
    /// Runs a workbook through the real extractor and a chunker configured the way the application's is,
    /// rather than the reduced budget the integration host uses for upload tests.
    /// </summary>
    private static async Task<IReadOnlyList<TextChunkDto>> ChunkWorkbookAsync()
    {
        var chunker = new TokenTextChunker(
            Options.Create(new DocumentOptions
            {
                Chunking = new ChunkingOptions { MaxTokens = 512, OverlapTokens = 128 }
            }),
            Options.Create(new AzureOpenAIOptions { EmbeddingModel = "text-embedding-3-small" }),
            NullLogger<TokenTextChunker>.Instance);

        var extractor = new SpreadsheetTextExtractor(
            NullLogger<SpreadsheetTextExtractor>.Instance, Options.Create(new SheetOptions()), chunker);

        var content = SpreadsheetFixture.CreateWorkbook(sheetCount: 1, rowsPerSheet: 400);
        var file = new FileDto
        {
            FileName = "budget.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Length = content.Length,
            Content = content
        };

        var segments = await extractor.ExtractAsync(file, progress: null, TestContext.Current.CancellationToken);

        return chunker.Chunk(segments, TestContext.Current.CancellationToken);
    }

    private static SeedChunk Chunk(int index, string text, double similarity) =>
        new(index, text, SeedChunk.Blend(QueryAxis, OtherAxis, similarity), 1);

    private async Task<Guid> AddConversationAsync() =>
        await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<DocumentSearchResult> SearchAsync(
        Guid conversationId, string query, DocumentRetrievalOptions options)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var service = new DocumentRetrievalService(
            NullLogger<DocumentRetrievalService>.Instance,
            _embeddingGenerator,
            scope.ServiceProvider.GetRequiredService<ITextChunker>(),
            Options.Create(options),
            scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>());

        var documentScope = await service.GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        return await service.SearchAsync(documentScope, query, null, TestContext.Current.CancellationToken);
    }
}
