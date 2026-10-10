using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the parts of <see cref="DocumentRetrievalService"/> that decide what the model sees:
/// scope resolution, term extraction, rank fusion, neighbour expansion, passage merging and the
/// token budget.
/// </summary>
/// <remarks>
/// The SQL passes themselves are absent by necessity — the SQLite fixture strips
/// <c>SqlVector&lt;float&gt;</c> from the model, so <c>VECTOR_DISTANCE</c> cannot run here. Those live in
/// <c>Enterprise.Gpt.Integration.Test</c> against SQL Server 2025.
/// </remarks>
public sealed class DocumentRetrievalServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly ITextChunker _textChunker = Substitute.For<ITextChunker>();
    private readonly DocumentRetrievalOptions _options = new();

    public DocumentRetrievalServiceTests()
    {
        _textChunker.OverlapTokens.Returns(128);

        // A word is roughly a token, which is close enough for budgeting assertions and keeps the
        // expected numbers in the tests readable.
        _textChunker.CountTokens(Arg.Any<string>())
            .Returns(call => call.ArgAt<string>(0).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    public void Dispose() => _fixture.Dispose();

    private DocumentRetrievalService CreateService() => new(
        NullLogger<DocumentRetrievalService>.Instance,
        _embeddingGenerator,
        _textChunker,
        Options.Create(_options),
        _fixture.Context);

    #region Scope resolution

    [Fact]
    public async Task GetScopeAsync_ConversationWithDocuments_ReturnsThemInUploadOrder()
    {
        var conversationId = await AddConversationAsync();
        await AddConversationDocumentAsync(conversationId, "first.pdf", ageMinutes: 10);
        await AddConversationDocumentAsync(conversationId, "second.pdf", ageMinutes: 5);

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.True(scope.HasDocuments);
        Assert.Equal(["first.pdf", "second.pdf"], scope.Documents.Select(d => d.Name));
        Assert.All(scope.Documents, d => Assert.Equal(DocumentSource.Conversation, d.Source));
        Assert.Null(scope.ProjectId);
    }

    [Fact]
    public async Task GetScopeAsync_GeneratedDocument_IsExcluded()
    {
        var conversationId = await AddConversationAsync();
        await AddConversationDocumentAsync(conversationId, "uploaded.pdf");
        await AddConversationDocumentAsync(conversationId, "made-for-you.xlsx", type: ConversationDocumentTypes.Generated);

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        // A file the assistant made is never a source it can search, summarize or cite — and its name
        // never reaches the retrieval prompt either.
        Assert.Equal(["uploaded.pdf"], scope.Documents.Select(d => d.Name));
        Assert.Equal(["uploaded.pdf"], scope.DocumentNames);
    }

    [Fact]
    public async Task GetScopeAsync_OnlyGeneratedDocuments_ReportsNoDocumentsAtAll()
    {
        var conversationId = await AddConversationAsync();
        await AddConversationDocumentAsync(conversationId, "made-for-you.docx", type: ConversationDocumentTypes.Generated);

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        // HasDocuments is what attaches the retrieval and summarization tools, so a conversation whose
        // only file is generated must look exactly like one with no files.
        Assert.False(scope.HasDocuments);
        Assert.Empty(scope.Documents);
    }

    [Fact]
    public async Task GetScopeAsync_DeactivatedDocument_IsExcluded()
    {
        var conversationId = await AddConversationAsync();
        await AddConversationDocumentAsync(conversationId, "live.pdf");
        await AddConversationDocumentAsync(conversationId, "deleted.pdf", deactivated: true);

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.Equal(["live.pdf"], scope.Documents.Select(d => d.Name));
    }

    [Fact]
    public async Task GetScopeAsync_ConversationInProject_IncludesProjectDocuments()
    {
        var projectId = await AddProjectAsync();
        var conversationId = await AddConversationAsync(projectId);
        await AddConversationDocumentAsync(conversationId, "conversation.pdf");
        await AddProjectDocumentAsync(projectId, "project.pdf");

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        Assert.Equal(projectId, scope.ProjectId);
        Assert.Equal(["conversation.pdf", "project.pdf"], scope.Documents.Select(d => d.Name));
        Assert.Equal(DocumentSource.Project, scope.Documents.Single(d => d.Name == "project.pdf").Source);
    }

    [Fact]
    public async Task GetScopeAsync_DeactivatedProject_DropsTheProjectAndItsDocuments()
    {
        var projectId = await AddProjectAsync(deactivated: true);
        var conversationId = await AddConversationAsync(projectId);
        await AddProjectDocumentAsync(projectId, "project.pdf");

        var scope = await CreateService().GetScopeAsync(conversationId, TestContext.Current.CancellationToken);

        // Nulled rather than merely filtered: the project half of every retrieval query is gated on the
        // identifier, so dropping it here is what keeps those documents unreachable for the whole turn.
        Assert.Null(scope.ProjectId);
        Assert.False(scope.HasDocuments);
    }

    [Fact]
    public async Task GetScopeAsync_AnotherConversationsDocuments_AreNotReturned()
    {
        var mine = await AddConversationAsync();
        var theirs = await AddConversationAsync();
        await AddConversationDocumentAsync(theirs, "theirs.pdf");

        var scope = await CreateService().GetScopeAsync(mine, TestContext.Current.CancellationToken);

        Assert.False(scope.HasDocuments);
    }

    #endregion

    #region Search guards

    [Fact]
    public async Task SearchAsync_NoDocumentsInScope_ReturnsEmptyWithoutEmbedding()
    {
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null, []);

        var result = await CreateService().SearchAsync(scope, "anything", null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ResultCount);
        Assert.Empty(result.Results);
        Assert.NotNull(result.Note);
        await _embeddingGenerator.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsEmptyWithoutEmbedding()
    {
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null,
            [new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "handbook.pdf")]);

        var result = await CreateService().SearchAsync(scope, "   ", null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ResultCount);
        Assert.Equal(["handbook.pdf"], result.AvailableDocuments);
        await _embeddingGenerator.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchAsync_QueryLongerThanTheCap_IsNotRunAndSaysWhy()
    {
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null,
            [new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "handbook.pdf")]);

        var result = await CreateService().SearchAsync(
            scope, new string('a', 2000), null, TestContext.Current.CancellationToken);

        // The query is model-supplied and goes straight to a billed embedding deployment, so a runaway
        // generation must not become a large embedding request.
        Assert.Equal(0, result.ResultCount);
        Assert.NotNull(result.Note);
        Assert.Contains("1024", result.Note, StringComparison.Ordinal);
        await _embeddingGenerator.DidNotReceiveWithAnyArgs()
            .GenerateAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchAsync_OverlongQueryEndingOnASurrogatePair_EchoesWholeCharacters()
    {
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null,
            [new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "handbook.pdf")]);

        // Cut exactly where the 1024th char would be the high half of a pair.
        var query = new string('a', 1023) + "\U0001F600" + new string('b', 100);

        var result = await CreateService().SearchAsync(scope, query, null, TestContext.Current.CancellationToken);

        Assert.Equal(1023, result.Query.Length);
        Assert.DoesNotContain(result.Query, c => char.IsSurrogate(c));
    }

    #endregion

    #region Term extraction

    [Fact]
    public void ExtractTerms_DropsStopWordsAndVeryShortTokens()
    {
        var terms = DocumentRetrievalService.ExtractTerms("What is the refund policy for a customer?", 8);

        Assert.Contains("refund", terms);
        Assert.Contains("policy", terms);
        Assert.Contains("customer", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("is", terms);
        Assert.DoesNotContain("for", terms);
    }

    [Fact]
    public void ExtractTerms_KeepsShortTokensThatContainDigits()
    {
        var terms = DocumentRetrievalService.ExtractTerms("does v2 support 5g", 8);

        Assert.Contains("v2", terms);
        Assert.Contains("5g", terms);
    }

    [Fact]
    public void ExtractTerms_RanksIdentifiersAheadOfWords()
    {
        var terms = DocumentRetrievalService.ExtractTerms("escalation procedure for error ORA-01555", 8);

        // Identifiers discriminate between chunks far better than prose does, and the cap keeps only
        // the front of this list when a query is long.
        Assert.Equal("01555", terms[0]);
    }

    [Fact]
    public void ExtractTerms_DeduplicatesAndHonoursTheCap()
    {
        var terms = DocumentRetrievalService.ExtractTerms("refund refund policy invoice payment receipt", 3);

        Assert.Equal(3, terms.Count);
        Assert.Distinct(terms);
    }

    [Theory]
    [InlineData("", 8)]
    [InlineData("   ", 8)]
    [InlineData("the a of", 8)]
    [InlineData("refund", 0)]
    public void ExtractTerms_NothingWorthSearchingFor_ReturnsEmpty(string query, int maxTerms)
    {
        Assert.Empty(DocumentRetrievalService.ExtractTerms(query, maxTerms));
    }

    [Fact]
    public void EscapeLikePattern_EscapesWildcardsAndTheEscapeCharacter()
    {
        Assert.Equal(@"100\%", DocumentRetrievalService.EscapeLikePattern("100%"));
        Assert.Equal(@"a\_b", DocumentRetrievalService.EscapeLikePattern("a_b"));
        Assert.Equal(@"\[x\]", DocumentRetrievalService.EscapeLikePattern("[x]"));
        Assert.Equal(@"a\\b", DocumentRetrievalService.EscapeLikePattern(@"a\b"));
    }

    [Fact]
    public void EscapeLikePattern_OrdinaryTerm_IsUnchanged()
    {
        Assert.Equal("refund", DocumentRetrievalService.EscapeLikePattern("refund"));
    }

    #endregion

    #region Rank fusion

    [Fact]
    public void SelectHits_ChunkFoundByBothPasses_OutranksEitherPassAlone()
    {
        var both = NewKey(index: 1);
        var denseOnly = NewKey(index: 2);
        var lexicalOnly = NewKey(index: 3);

        var dense = new List<ChunkCandidate>
        {
            Dense(denseOnly, 0.10),
            Dense(both, 0.20)
        };
        var lexical = new List<ChunkCandidate>
        {
            Lexical(lexicalOnly, matchCount: 3),
            Lexical(both, matchCount: 3)
        };

        var selected = CreateService().SelectHits(dense, lexical, termCount: 3);

        Assert.Equal(both, selected[0].Key);
    }

    [Fact]
    public void SelectHits_VectorMatchBeyondTheDistanceGate_IsDropped()
    {
        _options.MaxDistance = 0.5;
        var near = NewKey(index: 1);
        var far = NewKey(index: 2);

        var selected = CreateService().SelectHits([Dense(near, 0.40), Dense(far, 0.90)], [], termCount: 0);

        Assert.Equal([near], selected.Select(s => s.Key));
    }

    [Fact]
    public void SelectHits_KeywordOnlyMatchOnASingleTerm_IsDroppedWhenSeveralTermsWereSearched()
    {
        var weak = NewKey(index: 1);
        var strong = NewKey(index: 2);

        var selected = CreateService().SelectHits([], [Lexical(weak, 1), Lexical(strong, 2)], termCount: 3);

        Assert.Equal([strong], selected.Select(s => s.Key));
    }

    [Fact]
    public void SelectHits_KeywordOnlyMatchOnTheOnlyTerm_IsKept()
    {
        var only = NewKey(index: 1);

        var selected = CreateService().SelectHits([], [Lexical(only, 1)], termCount: 1);

        Assert.Equal([only], selected.Select(s => s.Key));
    }

    [Fact]
    public void SelectHits_OneDocumentDominating_IsCappedSoOthersSurvive()
    {
        _options.MaxPassagesPerDocument = 2;
        _options.MaxResults = 10;
        var loud = Guid.NewGuid();
        var quiet = Guid.NewGuid();

        var dense = new List<ChunkCandidate>
        {
            Dense(new ChunkKey(loud, DocumentSource.Conversation, 0), 0.10),
            Dense(new ChunkKey(loud, DocumentSource.Conversation, 1), 0.11),
            Dense(new ChunkKey(loud, DocumentSource.Conversation, 2), 0.12),
            Dense(new ChunkKey(quiet, DocumentSource.Conversation, 0), 0.13)
        };

        var selected = CreateService().SelectHits(dense, [], termCount: 0);

        Assert.Equal(3, selected.Count);
        Assert.Equal(2, selected.Count(s => s.Key.DocumentId == loud));
        Assert.Equal(1, selected.Count(s => s.Key.DocumentId == quiet));
    }

    [Fact]
    public void SelectHits_MoreMatchesThanRequested_StopsAtMaxResults()
    {
        _options.MaxResults = 2;
        _options.MaxPassagesPerDocument = 10;

        var dense = Enumerable.Range(0, 6).Select(i => Dense(NewKey(i), 0.1 + (i * 0.01))).ToList();

        Assert.Equal(2, CreateService().SelectHits(dense, [], termCount: 0).Count);
    }

    [Fact]
    public void SelectHits_SameChunkFromBothPasses_KeepsTheVectorDistance()
    {
        var key = NewKey(index: 1);

        var selected = CreateService().SelectHits([Dense(key, 0.25)], [Lexical(key, 2)], termCount: 2);

        var hit = Assert.Single(selected);
        Assert.Equal(0.25, hit.Distance);
        Assert.Equal(2, hit.MatchCount);
    }

    #endregion

    #region Neighbour expansion

    [Fact]
    public void ExpandToNeighbours_WidensEachHitByTheWindow()
    {
        _options.NeighborWindow = 1;
        var documentId = Guid.NewGuid();

        var keys = CreateService().ExpandToNeighbours(
            [new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 5), 1.0, 0.1, 0)]);

        Assert.Equal([4, 5, 6], keys.Select(k => k.Index).Order());
    }

    [Fact]
    public void ExpandToNeighbours_FirstChunkOfADocument_DoesNotAskForANegativeIndex()
    {
        _options.NeighborWindow = 2;
        var documentId = Guid.NewGuid();

        var keys = CreateService().ExpandToNeighbours(
            [new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.1, 0)]);

        Assert.Equal([0, 1, 2], keys.Select(k => k.Index).Order());
    }

    [Fact]
    public void ExpandToNeighbours_OverlappingWindows_AreRequestedOnce()
    {
        _options.NeighborWindow = 1;
        var documentId = Guid.NewGuid();

        var keys = CreateService().ExpandToNeighbours(
        [
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 3), 1.0, 0.1, 0),
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 4), 0.9, 0.2, 0)
        ]);

        Assert.Equal([2, 3, 4, 5], keys.Select(k => k.Index).Order());
    }

    [Fact]
    public void ExpandToNeighbours_SameIndexInBothTables_StaysTwoDistinctChunks()
    {
        _options.NeighborWindow = 0;
        var documentId = Guid.NewGuid();

        var keys = CreateService().ExpandToNeighbours(
        [
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 7), 1.0, 0.1, 0),
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Project, 7), 1.0, 0.1, 0)
        ]);

        Assert.Equal(2, keys.Count);
    }

    #endregion

    #region Overlap stripping

    [Fact]
    public void AppendWithoutOverlap_SharedSeam_IsWrittenOnce()
    {
        var accumulated = new StringBuilder("The quick brown fox jumps over the lazy dog and keeps running.");

        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "over the lazy dog and keeps running. Then it stops.", 512);

        Assert.Equal("The quick brown fox jumps over the lazy dog and keeps running. Then it stops.", accumulated.ToString());
    }

    [Fact]
    public void AppendWithoutOverlap_NoSeam_FallsBackToAParagraphBreak()
    {
        var accumulated = new StringBuilder("First chunk of prose.");

        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "Entirely unrelated continuation.", 512);

        Assert.Equal("First chunk of prose.\n\nEntirely unrelated continuation.", accumulated.ToString());
    }

    [Fact]
    public void AppendWithoutOverlap_SeamShorterThanTheNoiseFloor_IsNotTreatedAsOverlap()
    {
        // "dog." is far too short to be real overlap; treating it as such would delete real text.
        var accumulated = new StringBuilder("A sentence ending with dog.");

        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "dog. A different sentence.", 512);

        Assert.Equal("A sentence ending with dog.\n\ndog. A different sentence.", accumulated.ToString());
    }

    [Fact]
    public void AppendWithoutOverlap_RepeatedPhraseInsideTheSeam_PrefersTheLongestMatch()
    {
        var accumulated = new StringBuilder("alpha beta gamma delta epsilon zeta eta theta");

        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "gamma delta epsilon zeta eta theta iota", 512);

        Assert.Equal("alpha beta gamma delta epsilon zeta eta theta iota", accumulated.ToString());
    }

    [Fact]
    public void AppendWithoutOverlap_EmptyAccumulator_TakesTheTextWhole()
    {
        var accumulated = new StringBuilder();

        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "Only chunk.", 512);

        Assert.Equal("Only chunk.", accumulated.ToString());
    }

    [Fact]
    public void AppendWithoutOverlap_SeamLongerThanTheSearchWindow_IsNotSearchedFor()
    {
        var accumulated = new StringBuilder("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa shared tail text here");

        // A window of four characters cannot see a twenty-character seam, so the fallback applies
        // rather than a partial and wrong strip.
        DocumentRetrievalService.AppendWithoutOverlap(accumulated, "shared tail text here and more", 4);

        Assert.StartsWith("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa shared tail text here\n\n", accumulated.ToString());
    }

    #endregion

    #region Passage assembly

    [Fact]
    public void BuildPassages_ContiguousChunks_MergeIntoOnePassage()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 1), 1.0, 0.2, 0) };
        var chunks = new[]
        {
            Chunk(documentId, 0, "Refunds are handled by the finance team.", page: 4, distance: 0.5),
            Chunk(documentId, 1, "Refunds are issued within 30 days of the request.", page: 4, distance: 0.2),
            Chunk(documentId, 2, "Escalations go to the duty manager.", page: 5, distance: 0.6)
        };

        var passages = CreateService().BuildPassages(scope, hits, chunks);

        var passage = Assert.Single(passages);
        Assert.Equal("handbook.pdf p.4", passage.Citation);
        Assert.Equal(4, passage.Page);
        Assert.Contains("finance team", passage.Text);
        Assert.Contains("30 days", passage.Text);
        Assert.Contains("duty manager", passage.Text);
    }

    [Fact]
    public void BuildPassages_GapBetweenChunks_SplitsIntoSeparatePassages()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[]
        {
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 1), 1.0, 0.2, 0),
            new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 9), 0.9, 0.3, 0)
        };
        var chunks = new[]
        {
            Chunk(documentId, 1, "Near the front.", page: 1, distance: 0.2),
            Chunk(documentId, 9, "Much later on.", page: 7, distance: 0.3)
        };

        var passages = CreateService().BuildPassages(scope, hits, chunks);

        Assert.Equal(2, passages.Count);
        Assert.Equal(["handbook.pdf p.1", "handbook.pdf p.7"], passages.Select(p => p.Citation));
    }

    [Fact]
    public void BuildPassages_RunOfNeighboursWithNoMatch_IsDropped()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 5), 1.0, 0.2, 0) };
        var chunks = new[]
        {
            Chunk(documentId, 1, "A neighbour of nothing.", page: 1, distance: 0.9),
            Chunk(documentId, 5, "The actual match.", page: 3, distance: 0.2)
        };

        var passages = CreateService().BuildPassages(scope, hits, chunks);

        var passage = Assert.Single(passages);
        Assert.Equal("The actual match.", passage.Text);
    }

    [Fact]
    public void BuildPassages_ScoresOnTheMatchNotTheNeighbour()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 1), 1.0, 0.4, 0) };
        var chunks = new[]
        {
            Chunk(documentId, 1, "The match.", page: 1, distance: 0.4),
            Chunk(documentId, 2, "A much closer neighbour.", page: 1, distance: 0.05)
        };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks));

        // 1 - 0.4, not 1 - 0.05: a neighbour was pulled in for context and must not flatter the score.
        Assert.Equal(0.6, passage.Score, 3);
    }

    [Fact]
    public void BuildPassages_PageLessFormat_CitesTheFileNameAlone()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "notes.txt");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.2, 0) };
        var chunks = new[] { Chunk(documentId, 0, "Some note.", page: null, distance: 0.2) };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks));

        Assert.Equal("notes.txt", passage.Citation);
        Assert.Null(passage.Page);
    }

    [Fact]
    public void BuildPassages_KeywordOnlyMatch_OutranksAFartherVectorMatchItBeatOnFusedScore()
    {
        var keyword = Guid.NewGuid();
        var vector = Guid.NewGuid();
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null,
        [
            new RetrievableDocument(keyword, DocumentSource.Conversation, "runbook.pdf"),
            new RetrievableDocument(vector, DocumentSource.Conversation, "essay.pdf")
        ]);

        // A keyword-only hit has no distance until read-back, and by construction its distance is worse
        // than every hit that passed the gate. Ordering passages by distance would therefore rank the
        // exact-token match — the whole reason the keyword pass exists — last, every single time.
        var hits = new[]
        {
            new ScoredChunk(new ChunkKey(keyword, DocumentSource.Conversation, 0), 0.9, null, 3),
            new ScoredChunk(new ChunkKey(vector, DocumentSource.Conversation, 0), 0.2, 0.30, 0)
        };
        var chunks = new[]
        {
            Chunk(keyword, 0, "Error ORA-01555: snapshot too old.", page: 4, distance: 0.95),
            Chunk(vector, 0, "A general discussion of database ageing.", page: 2, distance: 0.30)
        };

        var passages = CreateService().BuildPassages(scope, hits, chunks);

        Assert.Equal(["runbook.pdf", "essay.pdf"], passages.Select(p => p.DocumentName));
    }

    [Fact]
    public void BuildPassages_CitesTheMatchsPageNotTheNeighboursPage()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 2), 1.0, 0.2, 0) };
        var chunks = new[]
        {
            Chunk(documentId, 1, "The tail of the previous page.", page: 4, distance: 0.8),
            Chunk(documentId, 2, "The match, at the top of page five.", page: 5, distance: 0.2)
        };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks));

        // The model is told to quote the citation verbatim, so taking the run's first page would put a
        // wrong page reference in front of the user.
        Assert.Equal(5, passage.Page);
        Assert.Equal("handbook.pdf p.5", passage.Citation);
    }

    [Fact]
    public void SpreadsheetExtensions_NameEveryExtractorThatReportsSheets()
    {
        // Citation reads the extension, not the extractor, so a third sheet-reporting format would
        // silently keep citing its sheet ordinals as page numbers.
        var implementations = typeof(ISheetStructureExtractor).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(ISheetStructureExtractor)))
            .Select(t => t.Name)
            .Order();

        Assert.Equal([nameof(CsvTextExtractor), nameof(SpreadsheetTextExtractor)], implementations);
        Assert.Equal([".csv", ".xlsx"], DocumentRetrievalService.SpreadsheetExtensions.Order());
    }

    [Fact]
    public void BuildPassages_SpreadsheetChunk_CitesItsSheetByName()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "budget.xlsx");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.2, 0) };
        var chunks = new[] { Chunk(documentId, 0, "SKU | Region\nW-1 | East", page: 3, distance: 0.2) };
        var sheetNames = new Dictionary<(Guid DocumentId, int SheetIndex), string> { [(documentId, 3)] = "Regional Revenue" };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks, sheetNames));

        Assert.Equal("budget.xlsx — Regional Revenue", passage.Citation);
        Assert.Equal(3, passage.Page);
    }

    [Fact]
    public void BuildPassages_SpreadsheetChunkWithNoStoredSheet_CitesTheFileNameRatherThanAPage()
    {
        // A sheet ordinal is not a page, so a document ingested before sheets were stored has to lose
        // the reference rather than claim one the file does not have.
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "orders.csv");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.2, 0) };
        var chunks = new[] { Chunk(documentId, 0, "SKU | Region", page: 1, distance: 0.2) };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks));

        Assert.Equal("orders.csv", passage.Citation);
    }

    [Fact]
    public void BuildPassages_ProseDocument_IsUnaffectedByASheetNameLookup()
    {
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "handbook.pdf");
        var hits = new[] { new ScoredChunk(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.2, 0) };
        var chunks = new[] { Chunk(documentId, 0, "Refunds are issued within 30 days.", page: 12, distance: 0.2) };
        var sheetNames = new Dictionary<(Guid DocumentId, int SheetIndex), string> { [(documentId, 12)] = "Never Used" };

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks, sheetNames));

        Assert.Equal("handbook.pdf p.12", passage.Citation);
    }

    [Fact]
    public void BuildPassages_SeveralDocuments_AreOrderedByBestMatch()
    {
        var far = Guid.NewGuid();
        var near = Guid.NewGuid();
        var scope = new DocumentRetrievalScope(Guid.NewGuid(), null,
        [
            new RetrievableDocument(far, DocumentSource.Conversation, "far.pdf"),
            new RetrievableDocument(near, DocumentSource.Conversation, "near.pdf")
        ]);
        var hits = new[]
        {
            new ScoredChunk(new ChunkKey(far, DocumentSource.Conversation, 0), 1.0, 0.7, 0),
            new ScoredChunk(new ChunkKey(near, DocumentSource.Conversation, 0), 1.0, 0.1, 0)
        };
        var chunks = new[]
        {
            Chunk(far, 0, "Distant.", page: 1, distance: 0.7),
            Chunk(near, 0, "Close.", page: 1, distance: 0.1)
        };

        var passages = CreateService().BuildPassages(scope, hits, chunks);

        Assert.Equal(["near.pdf", "far.pdf"], passages.Select(p => p.DocumentName));
    }

    #endregion

    #region Row-window corpora

    /// <summary>
    /// A workbook is one document however many row windows it was chunked into, so the per-document cap
    /// is what stops a header-repeating corpus crowding prose out of a mixed result set.
    /// </summary>
    [Fact]
    public void SelectHits_EveryRowWindowOfAWorkbookMatching_StillLeavesRoomForProse()
    {
        var workbook = Guid.NewGuid();
        var handbook = Guid.NewGuid();

        List<ChunkCandidate> dense =
        [
            .. Enumerable.Range(0, 12)
                .Select(i => Dense(new ChunkKey(workbook, DocumentSource.Conversation, i), 0.10 + (i * 0.001))),
            Dense(new ChunkKey(handbook, DocumentSource.Conversation, 0), 0.30)
        ];

        var selected = CreateService().SelectHits(dense, [], termCount: 0);

        Assert.Equal(_options.MaxPassagesPerDocument, selected.Count(s => s.Key.DocumentId == workbook));
        Assert.Single(selected, s => s.Key.DocumentId == handbook);
    }

    /// <summary>
    /// The gate is disjunctive, so lowering the distance threshold does not hold row windows back: they
    /// are full of identifiers and enter through the keyword arm, which has no distance to test.
    /// </summary>
    [Fact]
    public void SelectHits_RowWindowMatchedByKeyword_SurvivesHoweverTightTheDistanceGateIs()
    {
        _options.MaxDistance = 0.01;
        var window = NewKey(index: 4);

        var selected = CreateService().SelectHits([], [Lexical(window, matchCount: 2)], termCount: 2);

        Assert.Equal([window], selected.Select(s => s.Key));
    }

    /// <summary>
    /// Two adjacent row windows share their opening prefix, not a tail-to-head seam, so the merger has
    /// nothing to strip and the header is replayed once per window inside one passage — the real cost a
    /// row-window corpus imposes, and it is charged to the result token budget rather than to recall.
    /// </summary>
    [Fact]
    public void BuildPassages_AdjacentRowWindows_ReplayTheHeaderOncePerWindow()
    {
        const string Header = "Sheet: Regional Revenue\nSKU | Region | Revenue";
        var documentId = Guid.NewGuid();
        var scope = ScopeWith(documentId, "budget.xlsx");
        ScoredChunk[] hits = [new(new ChunkKey(documentId, DocumentSource.Conversation, 0), 1.0, 0.2, 0)];
        ChunkText[] chunks =
        [
            Chunk(documentId, 0, $"{Header}\nW-1 | East | 1200", page: null, distance: 0.2),
            Chunk(documentId, 1, $"{Header}\nW-2 | West | 900", page: null, distance: 0.3)
        ];

        var passage = Assert.Single(CreateService().BuildPassages(scope, hits, chunks));

        Assert.Equal(2, passage.Text.Split(Header).Length - 1);
    }

    #endregion

    #region Token budget

    [Fact]
    public void ApplyTokenBudget_EverythingFits_KeepsAllAndReportsNoTruncation()
    {
        _options.MaxResultTokens = 100;
        var passages = new[] { Passage("one two three"), Passage("four five") };

        var (kept, truncated) = CreateService().ApplyTokenBudget(passages);

        Assert.Equal(2, kept.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void ApplyTokenBudget_BudgetExhausted_DropsTheTailAndFlagsTruncation()
    {
        _options.MaxResultTokens = 4;
        var passages = new[] { Passage("one two three"), Passage("four five six"), Passage("seven") };

        var (kept, truncated) = CreateService().ApplyTokenBudget(passages);

        Assert.Single(kept);
        Assert.True(truncated);
    }

    [Fact]
    public void ApplyTokenBudget_FirstPassageAloneExceedsTheBudget_IsStillReturned()
    {
        _options.MaxResultTokens = 2;
        var passages = new[] { Passage("one two three four five") };

        var (kept, truncated) = CreateService().ApplyTokenBudget(passages);

        // Returning nothing because the single best match is long is a worse answer than overshooting
        // once, and a merged passage is bounded by the chunk size times the neighbour window anyway.
        Assert.Single(kept);
        Assert.False(truncated);
    }

    #endregion

    #region Helpers

    private static ChunkKey NewKey(int index) => new(Guid.NewGuid(), DocumentSource.Conversation, index);

    private static ChunkCandidate Dense(ChunkKey key, double distance) =>
        new(key.DocumentId, key.Source, key.Index, distance, MatchCount: 0);

    private static ChunkCandidate Lexical(ChunkKey key, int matchCount) =>
        new(key.DocumentId, key.Source, key.Index, Distance: null, matchCount);

    private static ChunkText Chunk(Guid documentId, int index, string text, int? page, double distance) =>
        new(new ChunkKey(documentId, DocumentSource.Conversation, index), page, text, distance);

    private static DocumentRetrievalScope ScopeWith(Guid documentId, string name) =>
        new(Guid.NewGuid(), null, [new RetrievableDocument(documentId, DocumentSource.Conversation, name)]);

    private static RetrievedPassage Passage(string text) =>
        new() { Citation = "doc.pdf", DocumentName = "doc.pdf", Page = 1, Score = 0.9, Text = text };

    private async Task<Guid> AddConversationAsync(Guid? projectId = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            ProjectId = projectId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.Id;
    }

    private async Task<Guid> AddProjectAsync(bool deactivated = false)
    {
        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = $"Project {Guid.NewGuid():N}",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        };

        using var ctx = _fixture.CreateContext();
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return project.Id;
    }

    private async Task AddConversationDocumentAsync(
        Guid conversationId, string name, bool deactivated = false, int ageMinutes = 0,
        ConversationDocumentTypes type = ConversationDocumentTypes.Uploaded)
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);

        using var ctx = _fixture.CreateContext();
        ctx.ConversationDocuments.Add(new ConversationDocument
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = KnownIds.SeedUserId,
            Type = type,
            Name = name,
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = $"path/{name}",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddProjectDocumentAsync(Guid projectId, string name)
    {
        var date = DateTimeOffset.UtcNow;

        using var ctx = _fixture.CreateContext();
        ctx.ProjectDocuments.Add(new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = KnownIds.SeedUserId,
            Name = name,
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = $"path/{name}",
            DateCreated = date,
            DateModified = date
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    #endregion
}
