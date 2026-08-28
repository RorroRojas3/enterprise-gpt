using System.Text;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers which of a conversation's files a run may work from, and which of the names in its
/// instruction it must refuse to guess at.
/// </summary>
public sealed class FileAgentDocumentReaderTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IBlobStorageService _blobStorageService = Substitute.For<IBlobStorageService>();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListNamesAsync_AConversationsFiles_ReturnsThemOldestFirst()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "first.docx", ageMinutes: 10);
        await AddDocumentAsync(conversationId, "second.xlsx", ageMinutes: 5);

        var names = await CreateReader()
            .ListNamesAsync(conversationId, KnownIds.SeedUserId, TestContext.Current.CancellationToken);

        Assert.Equal(["first.docx", "second.xlsx"], names);
    }

    // The rule the download route enforces, read off the parent conversation rather than the document
    // row, so a generated file whose own UserId is the turn's does not become readable to someone else.
    [Fact]
    public async Task ListNamesAsync_AConversationTheCallerDoesNotOwn_ReturnsNothing()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "private.docx");

        var names = await CreateReader()
            .ListNamesAsync(conversationId, Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Empty(names);
    }

    [Fact]
    public async Task ResolveAsync_ANameInTheInstruction_MatchesThatDocument()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "report.docx");

        var resolution = await ResolveAsync(conversationId, "Turn report.docx into a pdf");

        var match = Assert.Single(resolution.Matched);
        Assert.Equal("report.docx", match.Name);
        Assert.Equal(".docx", match.Extension);
        Assert.Empty(resolution.Ambiguous);
        Assert.Empty(resolution.Unresolved);
    }

    // Shortest-first matching would mount the wrong file for an instruction that names the longer one.
    [Fact]
    public async Task ResolveAsync_OneNameContainedInAnother_PrefersTheLonger()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "report.docx");
        await AddDocumentAsync(conversationId, "final report.docx");

        var resolution = await ResolveAsync(conversationId, "Convert final report.docx to markdown");

        Assert.Equal("final report.docx", resolution.Matched[0].Name);
    }

    [Fact]
    public async Task ResolveAsync_TwoLiveDocumentsSharingAName_ReportsItAsAmbiguous()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "report.docx");
        await AddDocumentAsync(conversationId, "report.docx");

        var resolution = await ResolveAsync(conversationId, "Convert report.docx to markdown");

        Assert.Equal(["report.docx"], resolution.Ambiguous);
        Assert.Empty(resolution.Matched);
    }

    [Fact]
    public async Task ResolveAsync_ADeactivatedDuplicate_LeavesTheLiveOneUnambiguous()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "report.docx");
        await AddDocumentAsync(conversationId, "report.docx", deactivated: true);

        var resolution = await ResolveAsync(conversationId, "Convert report.docx to markdown");

        Assert.Empty(resolution.Ambiguous);
        Assert.Single(resolution.Matched);
    }

    // The token a file name yields is only its last whitespace-delimited segment, so demanding an exact
    // match against the available names would report every file whose name has a space as missing — and
    // the pre-flight refuses on an unresolved name before it ever looks at what matched.
    [Theory]
    [InlineData("final report.docx", "Convert final report.docx to markdown")]
    [InlineData("Q3 sales data.xlsx", "Turn Q3 sales data.xlsx into a pdf")]
    [InlineData("meeting notes 2026.txt", "Make meeting notes 2026.txt into a Word document")]
    public async Task ResolveAsync_ANameContainingSpaces_IsMatchedRatherThanReportedMissing(
        string fileName, string instruction)
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, fileName);

        var resolution = await ResolveAsync(conversationId, instruction);

        Assert.Empty(resolution.Unresolved);
        Assert.Equal(fileName, Assert.Single(resolution.Matched).Name);
    }

    // The other half of the space fix. A token satisfied by an unrelated name it merely occurs inside
    // would be neither matched nor refused, and the run would pay for a sandbox session with no files.
    [Theory]
    [InlineData("quarterly-report.docx", "Convert report.docx to pdf")]
    [InlineData("2026_notes.txt", "Turn notes.txt into a Word document")]
    public async Task ResolveAsync_ATokenOnlyOccurringInsideAnotherName_IsStillReportedMissing(
        string available, string instruction)
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, available);

        var resolution = await ResolveAsync(conversationId, instruction);

        Assert.NotEmpty(resolution.Unresolved);
        Assert.Empty(resolution.Matched);
    }

    [Fact]
    public async Task ResolveAsync_AFileNameThatMatchesNothing_ReportsItUnresolved()
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, "present.docx");

        var resolution = await ResolveAsync(conversationId, "Convert missing.xlsx to csv");

        Assert.Equal(["missing.xlsx"], resolution.Unresolved);
        Assert.Equal(["present.docx"], resolution.Available);
    }

    // A create request names no file at all, and reading an ordinary word as a missing one would refuse
    // it. Only a token already shaped like a file name of a format this platform handles counts.
    [Theory]
    [InlineData("Make me a spreadsheet of last quarter's revenue")]
    [InlineData("Write a summary document about the md release process")]
    [InlineData("Produce a deck. Text should be short.")]
    public async Task ResolveAsync_AnInstructionNamingNoFile_ReportsNothingUnresolved(string instruction)
    {
        var conversationId = await AddConversationAsync();

        var resolution = await ResolveAsync(conversationId, instruction);

        Assert.Empty(resolution.Unresolved);
        Assert.Empty(resolution.Matched);
    }

    [Fact]
    public async Task ReadAsync_AResolvedDocument_ReadsItFromTheContainerItsTypeBelongsTo()
    {
        var conversationId = await AddConversationAsync();
        var uploaded = await AddDocumentAsync(conversationId, "source.docx");
        var generated = await AddDocumentAsync(
            conversationId, "made.xlsx", type: ConversationDocumentTypes.Generated);

        _blobStorageService
            .DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("bytes"));

        var files = await CreateReader().ReadAsync(
            conversationId, KnownIds.SeedUserId, [uploaded, generated], TestContext.Current.CancellationToken);

        Assert.Equal(["source.docx", "made.xlsx"], files.Select(file => file.Name));
        await _blobStorageService.Received(1)
            .DownloadAsync("documents", "path/source.docx", Arg.Any<CancellationToken>());
        await _blobStorageService.Received(1)
            .DownloadAsync("generated-documents", "path/made.xlsx", Arg.Any<CancellationToken>());
    }

    // Authorized on its own terms rather than trusting whatever resolved the identifiers, so a stale id
    // from an earlier turn cannot read across a conversation boundary.
    [Fact]
    public async Task ReadAsync_ADocumentOutsideTheCallersConversation_ReadsNothing()
    {
        var conversationId = await AddConversationAsync();
        var documentId = await AddDocumentAsync(conversationId, "private.docx");

        var files = await CreateReader().ReadAsync(
            conversationId, Guid.NewGuid(), [documentId], TestContext.Current.CancellationToken);

        Assert.Empty(files);
        await _blobStorageService.DidNotReceive()
            .DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The seam the two halves meet at. Every other pre-flight test hand-builds its resolution, so a
    // resolver that reported a present file as missing would be invisible to all of them.
    [Theory]
    [InlineData("final report.docx", "Convert final report.docx to markdown")]
    [InlineData("quarterly deck.pptx", "Convert quarterly deck.pptx to markdown")]
    public async Task Preflight_AResolutionFromARealConversation_RunsRatherThanRefusing(
        string fileName, string instruction)
    {
        var conversationId = await AddConversationAsync();
        await AddDocumentAsync(conversationId, fileName);

        var resolution = await ResolveAsync(conversationId, instruction);

        Assert.Null(FileAgentPreflight.Evaluate(resolution, instruction, ConversionMatrix.Load()));
    }

    private FileAgentDocumentReader CreateReader() => new(
        NullLogger<FileAgentDocumentReader>.Instance,
        _blobStorageService,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DocumentStorage.DocumentsContainerKey] = "documents",
                [DocumentStorage.GeneratedContainerKey] = "generated-documents"
            })
            .Build(),
        _fixture.Context);

    private Task<FileAgentSourceResolution> ResolveAsync(Guid conversationId, string instruction) =>
        CreateReader().ResolveAsync(
            conversationId, KnownIds.SeedUserId, instruction, TestContext.Current.CancellationToken);

    private async Task<Guid> AddConversationAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.Id;
    }

    private async Task<Guid> AddDocumentAsync(
        Guid conversationId,
        string name,
        bool deactivated = false,
        int ageMinutes = 0,
        ConversationDocumentTypes type = ConversationDocumentTypes.Uploaded)
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
        var documentId = Guid.NewGuid();

        using var ctx = _fixture.CreateContext();
        ctx.ConversationDocuments.Add(new ConversationDocument
        {
            Id = documentId,
            ConversationId = conversationId,
            UserId = KnownIds.SeedUserId,
            Type = type,
            Name = name,
            Extension = Path.GetExtension(name),
            MimeType = "application/octet-stream",
            Size = 1024,
            Path = $"path/{name}",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated ? date : null
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return documentId;
    }
}
