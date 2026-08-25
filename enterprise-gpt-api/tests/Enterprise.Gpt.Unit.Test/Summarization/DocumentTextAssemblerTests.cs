using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Summarization;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Summarization;

public sealed class DocumentTextAssemblerTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITextChunker _textChunker = Substitute.For<ITextChunker>();
    private readonly DocumentTextAssembler _assembler;

    public DocumentTextAssemblerTests()
    {
        _textChunker.OverlapTokens.Returns(128);
        _assembler = new DocumentTextAssembler(_fixture.Context, _textChunker);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    #region AssembleAsync

    [Fact]
    public async Task AssembleAsync_ConversationDocument_ReadsChunksInIndexOrder()
    {
        var documentId = await AddConversationDocumentAsync(
            (2, "third."), (0, "first."), (1, "second."));

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal("first.\n\nsecond.\n\nthird.", text);
    }

    /// <summary>
    /// The two document families differ only in which table is read, so the project path has to
    /// produce what the conversation path does for identical chunk rows.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_ProjectDocument_ReusesTheSameReassembly()
    {
        var documentId = await AddProjectDocumentAsync((0, "first."), (1, "second."));

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Project, TestContext.Current.CancellationToken);

        Assert.Equal("first.\n\nsecond.", text);
    }

    /// <summary>
    /// The chunker overlaps adjacent chunks on purpose, so reassembling without stripping the seam
    /// would repeat a sentence at every chunk boundary.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_OverlappingChunks_StripsTheSeam()
    {
        const string Overlap = "the quick brown fox jumps over the lazy dog";
        var documentId = await AddConversationDocumentAsync(
            (0, $"opening line. {Overlap}"), (1, $"{Overlap} closing line."));

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal($"opening line. {Overlap} closing line.", text);
        Assert.Equal(1, CountOccurrences(text, Overlap));
    }

    /// <summary>
    /// A failed re-ingestion leaves the old chunk rows soft-deleted beside the new ones. Reading
    /// both would interleave two generations of the same document's text.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_SoftDeletedChunks_AreExcluded()
    {
        var documentId = await AddConversationDocumentAsync(
            (0, "kept."), (1, "also kept."));

        await DeactivateChunkAsync(documentId, index: 1);

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal("kept.", text);
    }

    /// <summary>
    /// A deleted document must not be summarizable through its chunk rows, which the cascades leave
    /// active in some paths.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_SoftDeletedDocument_ReturnsEmpty()
    {
        var documentId = await AddConversationDocumentAsync((0, "orphaned."));

        using (var ctx = _fixture.CreateContext())
        {
            var document = await ctx.ConversationDocuments.FindAsync(
                [documentId], TestContext.Current.CancellationToken);
            document!.DateDeactivated = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task AssembleAsync_NoChunks_ReturnsEmpty()
    {
        var documentId = await AddConversationDocumentAsync();

        var text = await _assembler.AssembleAsync(
            documentId, DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task AssembleAsync_UnknownDocument_ReturnsEmpty()
    {
        var text = await _assembler.AssembleAsync(
            Guid.NewGuid(), DocumentSource.Conversation, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, text);
    }

    #endregion

    #region Helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private async Task<Guid> AddConversationDocumentAsync(params (int Index, string Text)[] chunks)
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

        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            UserId = KnownIds.SeedUserId,
            Name = "document-under-test.pdf",
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = "documents/document-under-test.pdf",
            DateCreated = date,
            DateModified = date,
            Chunks = [.. chunks.Select(c => new ConversationDocumentChunk
            {
                Id = Guid.NewGuid(),
                Index = c.Index,
                Text = c.Text,
                TokenCount = c.Text.Length / 4,
                DateCreated = date,
                DateModified = date
            })]
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Id;
    }

    private async Task<Guid> AddProjectDocumentAsync(params (int Index, string Text)[] chunks)
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
            Name = "project-document-under-test.pdf",
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = "documents/project-document-under-test.pdf",
            DateCreated = date,
            DateModified = date,
            Chunks = [.. chunks.Select(c => new ProjectDocumentChunk
            {
                Id = Guid.NewGuid(),
                Index = c.Index,
                Text = c.Text,
                TokenCount = c.Text.Length / 4,
                DateCreated = date,
                DateModified = date
            })]
        };

        using var ctx = _fixture.CreateContext();
        ctx.Projects.Add(project);
        ctx.ProjectDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Id;
    }

    private async Task DeactivateChunkAsync(Guid documentId, int index)
    {
        using var ctx = _fixture.CreateContext();
        var chunk = ctx.ConversationDocumentChunks
            .First(c => c.ConversationDocumentId == documentId && c.Index == index);
        chunk.DateDeactivated = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    #endregion
}
