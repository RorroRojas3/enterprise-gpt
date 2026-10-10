using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

/// <summary>
/// Covers the seam between the engine and the money: that a stored summary is reused for nothing,
/// that a miss is billed exactly once at the summarizer's own rate, and that a digest is billed but
/// never stored.
/// </summary>
public sealed class DocumentSummaryServiceTests : IDisposable
{
    private const string Summary = "The handbook explains the refund process.";
    private const string DeploymentName = "rr-gpt5.6-luna";

    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IDocumentSummarizer _summarizer = Substitute.For<IDocumentSummarizer>();
    private readonly DocumentSummaryService _service;

    public DocumentSummaryServiceTests()
    {
        _summarizer
            .SummarizeDocumentAsync(Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(Run(Summary));

        _service = new DocumentSummaryService(
            _summarizer,
            _fixture.Context,
            _fixture.ScopeFactory,
            Options.Create(new SummarizationOptions { ModelId = KnownIds.SeedModelId }),
            NullLogger<DocumentSummaryService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_NoStoredSummary_SummarizesAndPersistsTheRun()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        var result = await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(Summary, result.Text);
        Assert.False(result.FromCache);
        Assert.Equal(3, result.ModelCallCount);

        using var ctx = _fixture.CreateContext();
        var row = await ctx.ConversationDocumentSummaries.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(documentId, row.ConversationDocumentId);
        Assert.Equal(Summary, row.Text);
        Assert.Equal(KnownIds.SeedModelId, row.ModelId);
        Assert.Equal(DeploymentName, row.DeploymentName);
        Assert.Equal(SummarizationPrompts.PromptVersion, row.PromptVersion);
        Assert.Equal(900, row.InputTokens);
        Assert.Equal(120, row.OutputTokens);
        Assert.Equal(3, row.ModelCallCount);
        Assert.Equal(2, row.MapUnitCount);
        Assert.Equal(1, row.CollapsePasses);
    }

    /// <summary>
    /// The cost-containment invariant: a run is billed at the pinned summarizer's own rate, never
    /// at whatever the conversation's selected chat model charges.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_NoStoredSummary_BillsOneRowAtTheSummarizersOwnRate()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var usage = await ctx.ConversationUsage.SingleAsync(TestContext.Current.CancellationToken);
        var model = await ctx.Models.SingleAsync(m => m.Id == KnownIds.SeedModelId, TestContext.Current.CancellationToken);

        Assert.Equal(ConversationUsageKinds.Summarization, usage.Kind);
        Assert.Equal(ConversationUsageStatuses.Completed, usage.Status);
        Assert.Equal(conversationId, usage.ConversationId);
        Assert.Equal(KnownIds.SeedModelId, usage.ModelId);
        Assert.Equal(model.ProviderId, usage.ProviderId);
        Assert.Equal(DeploymentName, usage.DeploymentName);
        Assert.Equal(900, usage.InputTokens);
        Assert.Equal(120, usage.OutputTokens);
        Assert.Equal(model.InputPricePerMillionTokens, usage.InputPricePerMillionTokens);
        Assert.Equal(model.OutputPricePerMillionTokens, usage.OutputPricePerMillionTokens);

        // Billed and never transcribed, so there is no transcript message to estimate a context
        // cost from. Null says "not transcribed" where zero would say "transcribed nothing".
        Assert.Null(usage.ContextTokens);
    }

    [Fact]
    public async Task GetOrCreateAsync_NoStoredSummary_MovesTheConversationsOwnCounters()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var conversation = await ctx.Conversations.SingleAsync(
            c => c.Id == conversationId, TestContext.Current.CancellationToken);

        Assert.Equal(900, conversation.InputTokens);
        Assert.Equal(120, conversation.OutputTokens);
    }

    /// <summary>
    /// The write must not ride the caller's context. It is shared with
    /// <c>ConversationService.PersistTurnAsync</c>, and EF accepts changes only on a successful
    /// save — so a failed write here would leave the summary and usage rows staged as
    /// <c>Added</c> for the turn's own save to re-attempt, double-billing the run or failing the
    /// turn's finalization after a fully streamed answer.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_NoStoredSummary_StagesNothingOnTheCallersContext()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Empty(_fixture.Context.ChangeTracker.Entries<ConversationDocumentSummary>());
        Assert.Empty(_fixture.Context.ChangeTracker.Entries<ConversationUsage>());
    }

    /// <summary>
    /// By the time the run returns, the money is already spent at the provider — and the token that
    /// would abort the write is the same one a client disconnect or the tool deadline signals.
    /// Finalization therefore runs uncancellable, exactly as the turn's own does.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_TokenCancelledAsTheRunReturns_StillPersistsAndBills()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        using var cts = new CancellationTokenSource();

        _summarizer
            .SummarizeDocumentAsync(Arg.Any<Guid>(), Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cts.CancelAsync();

                return Run(Summary);
            });

        var result = await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, cts.Token);

        Assert.Equal(Summary, result.Text);

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.ConversationDocumentSummaries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await ctx.ConversationUsage.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The cache-effectiveness invariant: a repeat request writes nothing and calls nothing.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_SummaryAlreadyStored_CostsNothingAtAll()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();

        await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        _summarizer.ClearReceivedCalls();

        var result = await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.True(result.FromCache);
        Assert.Equal(0, result.ModelCallCount);
        Assert.Equal(Summary, result.Text);

        await _summarizer.DidNotReceiveWithAnyArgs().SummarizeDocumentAsync(default, default, Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.ConversationUsage.CountAsync(TestContext.Current.CancellationToken));

        var conversation = await ctx.Conversations.SingleAsync(
            c => c.Id == conversationId, TestContext.Current.CancellationToken);
        Assert.Equal(900, conversation.InputTokens);
        Assert.Equal(120, conversation.OutputTokens);
    }

    /// <summary>
    /// A project document's summary is shared, so a second conversation reading it is a hit — that
    /// is what makes one canonical row per document worth having.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_ProjectDocumentAlreadySummarized_IsAHitForASecondConversation()
    {
        var (firstConversationId, documentId) = await AddProjectDocumentAsync();
        var secondConversationId = await AddConversationAsync();

        await _service.GetOrCreateAsync(
            firstConversationId, documentId, DocumentSource.Project, TestContext.Current.CancellationToken);

        var result = await _service.GetOrCreateAsync(
            secondConversationId, documentId, DocumentSource.Project, TestContext.Current.CancellationToken);

        Assert.True(result.FromCache);

        using var ctx = _fixture.CreateContext();
        Assert.Equal(1, await ctx.ProjectDocumentSummaries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await ctx.ConversationUsage.CountAsync(
            u => u.ConversationId == secondConversationId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The whole cache-invalidation mechanism: a document's text never changes, so a stored summary
    /// can only go stale when the prompts that produced it do.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_StoredSummaryFromAnOlderPromptVersion_RegeneratesInPlace()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();
        await AddStoredSummaryAsync(documentId, "Written under an older prompt.", promptVersion: "0");

        var result = await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.False(result.FromCache);
        Assert.Equal(Summary, result.Text);

        using var ctx = _fixture.CreateContext();

        // Overwritten, not appended to: the unique index is filtered on DateDeactivated, so a
        // second live row for the same document is a constraint violation rather than a version.
        var row = await ctx.ConversationDocumentSummaries.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Summary, row.Text);
        Assert.Equal(SummarizationPrompts.PromptVersion, row.PromptVersion);
    }

    /// <summary>
    /// A regeneration reports what the new run cost, not the old run's totals added to it.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_Regenerated_ReplacesTheRunTotalsRatherThanAddingToThem()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();
        await AddStoredSummaryAsync(documentId, "Older.", promptVersion: "0", inputTokens: 5_000, outputTokens: 4_000);

        await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var row = await ctx.ConversationDocumentSummaries.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(900, row.InputTokens);
        Assert.Equal(120, row.OutputTokens);
    }

    /// <summary>
    /// A summary deactivated alongside its document must not be served, and must not block the next
    /// one either.
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_StoredSummaryDeactivated_IsAMissAndInsertsBesideIt()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();
        await AddStoredSummaryAsync(documentId, "Deactivated.", deactivated: true);

        var result = await _service.GetOrCreateAsync(
            conversationId, documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.False(result.FromCache);

        using var ctx = _fixture.CreateContext();
        Assert.Equal(2, await ctx.ConversationDocumentSummaries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await ctx.ConversationDocumentSummaries.CountAsync(
            s => !s.DateDeactivated.HasValue, TestContext.Current.CancellationToken));
    }

    #endregion

    #region CreateDigestAsync

    [Fact]
    public async Task CreateDigestAsync_NoDocumentsInScope_RefusesRatherThanCallingTheSummarizer()
    {
        var conversationId = await AddConversationAsync();
        var scope = new DocumentRetrievalScope(conversationId, null, []);

        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateDigestAsync(
            conversationId, scope, TestContext.Current.CancellationToken));

        await _summarizer.DidNotReceiveWithAnyArgs().SummarizeDigestAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDigestAsync_ScopeAboveTheConfiguredMaximum_RefusesNamingTheLimit()
    {
        var conversationId = await AddConversationAsync();

        var service = new DocumentSummaryService(
            _summarizer,
            _fixture.Context,
            _fixture.ScopeFactory,
            Options.Create(new SummarizationOptions { ModelId = KnownIds.SeedModelId, MaxDigestDocuments = 1 }),
            NullLogger<DocumentSummaryService>.Instance);

        var scope = new DocumentRetrievalScope(conversationId, null,
        [
            new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "a.pdf"),
            new RetrievableDocument(Guid.NewGuid(), DocumentSource.Conversation, "b.pdf")
        ]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.CreateDigestAsync(
            conversationId, scope, TestContext.Current.CancellationToken));

        Assert.Contains("at most 1", exception.Message, StringComparison.Ordinal);
        await _summarizer.DidNotReceiveWithAnyArgs().SummarizeDocumentAsync(default, default, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The digest's whole cost argument: already-summarized documents contribute for nothing, so a
    /// two-document conversation with one summary costs one run plus the digest, not two plus one.
    /// </summary>
    [Fact]
    public async Task CreateDigestAsync_SomeDocumentsAlreadySummarized_OnlySummarizesTheRest()
    {
        var (conversationId, firstId) = await AddConversationDocumentAsync();
        var secondId = await AddConversationDocumentAsync(conversationId);

        await AddStoredSummaryAsync(firstId, "Already known.");

        _summarizer
            .SummarizeDigestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Run("Both documents concern refunds."));

        var scope = new DocumentRetrievalScope(conversationId, null,
        [
            new RetrievableDocument(firstId, DocumentSource.Conversation, "handbook.pdf"),
            new RetrievableDocument(secondId, DocumentSource.Conversation, "policy.pdf")
        ]);

        var digest = await _service.CreateDigestAsync(
            conversationId, scope, TestContext.Current.CancellationToken);

        Assert.Equal("Both documents concern refunds.", digest);

        await _summarizer.Received(1).SummarizeDocumentAsync(
            secondId, DocumentSource.Conversation, Arg.Any<CancellationToken>());
        await _summarizer.DidNotReceive().SummarizeDocumentAsync(
            firstId, Arg.Any<DocumentSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDigestAsync_Completes_BillsTheDigestButStoresNothing()
    {
        var (conversationId, documentId) = await AddConversationDocumentAsync();
        await AddStoredSummaryAsync(documentId, "Already known.");

        _summarizer
            .SummarizeDigestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Run("A digest."));

        var scope = new DocumentRetrievalScope(conversationId, null,
            [new RetrievableDocument(documentId, DocumentSource.Conversation, "handbook.pdf")]);

        await _service.CreateDigestAsync(conversationId, scope, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();

        // One row for the digest's own reduce call, and nothing at all for the cache hit under it.
        var usage = await ctx.ConversationUsage.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConversationUsageKinds.Summarization, usage.Kind);

        // A digest describes a scope, and the scope changes the moment a document is added.
        var summaries = await ctx.ConversationDocumentSummaries.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(summaries);
        Assert.Equal("Already known.", summaries[0].Text);
    }

    #endregion

    #region Helpers

    private static SummarizationRun Run(string summary) => new(
        summary,
        InputTokens: 900,
        OutputTokens: 120,
        ModelCallCount: 3,
        MapUnitCount: 2,
        CollapsePasses: 1,
        Truncated: false,
        KnownIds.SeedModelId,
        DeploymentName,
        SummarizationPrompts.PromptVersion);

    private async Task<Guid> AddConversationAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            ModelId = KnownIds.SeedModelId,
            Name = "conversation-under-test",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.Id;
    }

    private async Task<(Guid ConversationId, Guid DocumentId)> AddConversationDocumentAsync()
    {
        var conversationId = await AddConversationAsync();

        return (conversationId, await AddConversationDocumentAsync(conversationId));
    }

    private async Task<Guid> AddConversationDocumentAsync(Guid conversationId)
    {
        var date = DateTimeOffset.UtcNow;
        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = KnownIds.SeedUserId,
            Name = "document-under-test.pdf",
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = "documents/document-under-test.pdf",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Id;
    }

    private async Task<(Guid ConversationId, Guid DocumentId)> AddProjectDocumentAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = "project-under-test",
            DateCreated = date,
            DateModified = date
        };

        var document = new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            UserId = KnownIds.SeedUserId,
            Name = "shared.docx",
            Extension = ".docx",
            MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Size = 2048,
            Path = "documents/shared.docx",
            DateCreated = date,
            DateModified = date
        };

        using (var ctx = _fixture.CreateContext())
        {
            ctx.Projects.Add(project);
            ctx.ProjectDocuments.Add(document);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return (await AddConversationAsync(), document.Id);
    }

    private async Task AddStoredSummaryAsync(
        Guid documentId,
        string text,
        string? promptVersion = null,
        bool deactivated = false,
        long inputTokens = 10,
        long outputTokens = 5)
    {
        var date = DateTimeOffset.UtcNow;

        using var ctx = _fixture.CreateContext();
        ctx.ConversationDocumentSummaries.Add(new ConversationDocumentSummary
        {
            Id = Guid.NewGuid(),
            ConversationDocumentId = documentId,
            Text = text,
            ModelId = KnownIds.SeedModelId,
            DeploymentName = DeploymentName,
            PromptVersion = promptVersion ?? SummarizationPrompts.PromptVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelCallCount = 1,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        });

        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    #endregion
}
