using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class GeneratedDocumentServiceTests : IDisposable
{
    private const string GeneratedContainerName = "generated-documents";
    private const string DocumentsContainerName = "documents";

    private readonly SqliteDbContextFixture _fixture = new();
    private readonly IBlobStorageService _blobStorageService = Substitute.For<IBlobStorageService>();
    private readonly GeneratedDocumentService _service;

    public GeneratedDocumentServiceTests()
    {
        _service = CreateService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:DocumentsContainer"] = DocumentsContainerName,
                ["AzureStorage:GeneratedContainer"] = GeneratedContainerName
            })
            .Build());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task StoreAsync_Artifact_WritesToTheGeneratedContainerAndRecordsItAsGenerated()
    {
        var conversation = await AddConversationAsync();

        var stored = await _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "summary.xlsx", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        await _blobStorageService.Received(1).UploadAsync(
            GeneratedContainerName,
            $"{KnownIds.SeedUserId}/{conversation.Id}/{stored.Id}.xlsx",
            Arg.Any<byte[]>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        var document = await ctx.ConversationDocuments.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConversationDocumentTypes.Generated, document.Type);
        Assert.Equal("summary.xlsx", document.Name);
        Assert.Equal(".xlsx", document.Extension);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", document.MimeType);
        Assert.Equal(3, document.Size);
    }

    [Fact]
    public async Task StoreAsync_Artifact_LeavesNoChunksAndNoSheets()
    {
        var conversation = await AddConversationAsync();

        var stored = await _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "notes.md", [1]),
            TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();

        Assert.Empty(await ctx.ConversationDocumentChunks
            .Where(x => x.ConversationDocumentId == stored.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await ctx.ConversationDocumentSheets
            .Where(x => x.ConversationDocumentId == stored.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreAsync_Artifact_WritesOnAContextOfItsOwn()
    {
        var conversation = await AddConversationAsync();

        await _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "notes.txt", [1]),
            TestContext.Current.CancellationToken);

        // The caller's context never saw the insert, which is what keeps a failure here off the
        // turn's own finalization.
        Assert.Empty(_fixture.Context.ChangeTracker.Entries<ConversationDocument>());
    }

    [Fact]
    public async Task StoreAsync_ArtifactOverTheSizeLimit_IsRejectedBeforeAnythingIsStored()
    {
        var conversation = await AddConversationAsync();
        var service = CreateService(Configuration(), maxFileSizeBytes: 2);

        await Assert.ThrowsAsync<ValidationException>(() => service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "big.pdf", [1, 2, 3]),
            TestContext.Current.CancellationToken));

        await _blobStorageService.DidNotReceive().UploadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        Assert.Empty(await ctx.ConversationDocuments.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("script.py")]
    [InlineData("archive.zip")]
    [InlineData("macro.xlsm")]
    [InlineData("nameonly")]
    public async Task StoreAsync_FormatThisPlatformDoesNotProduce_IsRejected(string fileName)
    {
        var conversation = await AddConversationAsync();

        await Assert.ThrowsAsync<ValidationException>(() => _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, fileName, [1]),
            TestContext.Current.CancellationToken));

        await _blobStorageService.DidNotReceive().UploadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreAsync_EmptyArtifact_IsRejected()
    {
        var conversation = await AddConversationAsync();

        await Assert.ThrowsAsync<ValidationException>(() => _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "empty.csv", []),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreAsync_FileNameCarryingADirectory_KeepsOnlyTheFileName()
    {
        var conversation = await AddConversationAsync();

        var stored = await _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "/mnt/data/report.docx", [1]),
            TestContext.Current.CancellationToken);

        Assert.Equal("report.docx", stored.Name);
        Assert.DoesNotContain("mnt", stored.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreAsync_ConversationBelongingToSomeoneElse_IsRefusedAndRemovesTheBlob()
    {
        var other = await AddUserAsync();
        var conversation = await AddConversationAsync(userId: other.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "report.pdf", [1]),
            TestContext.Current.CancellationToken));

        // The bytes were written before the row was attempted, so a refused insert must take them
        // back out rather than leave content nothing references.
        await _blobStorageService.Received(1).DeleteAsync(
            GeneratedContainerName, Arg.Any<string>(), Arg.Any<CancellationToken>());

        using var ctx = _fixture.CreateContext();
        Assert.Empty(await ctx.ConversationDocuments.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreAsync_BlobRemovalAlsoFails_ReportsTheOriginalFailure()
    {
        var other = await AddUserAsync();
        var conversation = await AddConversationAsync(userId: other.Id);
        _blobStorageService
            .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("storage is unreachable"));

        await Assert.ThrowsAsync<NotFoundException>(() => _service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "report.pdf", [1]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoreAsync_GeneratedContainerNotConfigured_ReportsAConfigurationFailure()
    {
        var conversation = await AddConversationAsync();
        var service = CreateService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureStorage:DocumentsContainer"] = DocumentsContainerName })
            .Build());

        await Assert.ThrowsAsync<StorageNotConfiguredException>(() => service.StoreAsync(
            new GeneratedDocumentRequest(conversation.Id, KnownIds.SeedUserId, "report.pdf", [1]),
            TestContext.Current.CancellationToken));
    }

    private GeneratedDocumentService CreateService(IConfiguration configuration, long maxFileSizeBytes = 50L * 1024 * 1024) =>
        new(NullLogger<GeneratedDocumentService>.Instance,
            _blobStorageService,
            configuration,
            Options.Create(new DocumentOptions { MaxFileSizeBytes = maxFileSizeBytes }),
            _fixture.ScopeFactory);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:DocumentsContainer"] = DocumentsContainerName,
                ["AzureStorage:GeneratedContainer"] = GeneratedContainerName
            })
            .Build();

    private async Task<User> AddUserAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Other",
            LastName = "User",
            Email = "other.user@example.com",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private async Task<Conversation> AddConversationAsync(Guid? userId = null, DateTimeOffset? deactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }
}
