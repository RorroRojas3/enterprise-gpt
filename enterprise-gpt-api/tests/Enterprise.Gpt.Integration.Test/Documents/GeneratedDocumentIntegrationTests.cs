using System.Net;
using System.Net.Http.Json;
using System.Web;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Documents;

/// <summary>
/// The generated-file contract end to end: stored in its own container, downloadable in its own
/// format, and structurally absent from retrieval.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GeneratedDocumentIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string GeneratedContainer = "test-generated-documents";
    private const string UploadsContainer = "test-documents";

    private readonly IntegrationTestFixture _fixture = fixture;

    public async ValueTask InitializeAsync() =>
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task StoreAsync_Artifact_LandsInTheGeneratedContainerAndNotTheUploadsOne()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stored = await StoreAsync(conversationId, "regional-summary.xlsx", [1, 2, 3]);

        Assert.Contains(
            _fixture.Factory.BlobStorage.UploadedKeys,
            key => key.StartsWith($"{GeneratedContainer}/", StringComparison.Ordinal)
                && key.EndsWith($"{stored.Id}.xlsx", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _fixture.Factory.BlobStorage.UploadedKeys,
            key => key.StartsWith($"{UploadsContainer}/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoreAsync_Artifact_CreatesAGeneratedRowWithNoChunks()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stored = await StoreAsync(conversationId, "notes.md", [1]);

        using var scope = _fixture.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var document = await ctx.ConversationDocuments
            .AsNoTracking()
            .SingleAsync(x => x.Id == stored.Id, TestContext.Current.CancellationToken);

        Assert.Equal(ConversationDocumentTypes.Generated, document.Type);

        // The whole of the retrieval isolation: a document with no chunk rows contributes nothing to
        // the candidate set, which is a structural fact rather than a predicate anybody has to keep.
        Assert.Equal(0, await ctx.ConversationDocumentChunks
            .CountAsync(x => x.ConversationDocumentId == stored.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_GeneratedDocument_IsSignedAgainstItsOwnContainerWithItsOwnType()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var stored = await StoreAsync(conversationId, "regional-summary.xlsx", [1, 2, 3]);

        using var client = _fixture.Factory.CreateUserClient();
        var download = await client.GetFromJsonAsync<DocumentDownloadDto>(
            $"api/documents/conversations/{conversationId}/{stored.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(download);
        Assert.Equal("regional-summary.xlsx", download.FileName);
        Assert.Contains($"/{GeneratedContainer}/", download.DownloadUrl, StringComparison.Ordinal);

        var query = HttpUtility.ParseQueryString(new Uri(download.DownloadUrl).Query);
        Assert.Equal(
            "attachment; filename=\"regional-summary.xlsx\"; filename*=UTF-8''regional-summary.xlsx",
            query["rscd"]);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", query["rsct"]);
    }

    [Fact]
    public async Task Download_GeneratedDocumentOfAnotherUsersConversation_IsNotFound()
    {
        var conversationId = await _fixture.AddConversationAsync(
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await _fixture.AddConversationDocumentAsync(
            conversationId, "private.docx", [], userId: TestUsers.AdminUserId,
            type: ConversationDocumentTypes.Generated, cancellationToken: TestContext.Current.CancellationToken);

        using var client = _fixture.Factory.CreateUserClient();
        var response = await client.GetAsync(
            $"api/documents/conversations/{conversationId}/{documentId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StoreAsync_ConversationBelongingToSomeoneElse_StoresNothing()
    {
        var conversationId = await _fixture.AddConversationAsync(
            userId: TestUsers.AdminUserId, cancellationToken: TestContext.Current.CancellationToken);

        using var scope = _fixture.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IGeneratedDocumentService>();

        await Assert.ThrowsAsync<Service.Exceptions.NotFoundException>(() => service.StoreAsync(
            new GeneratedDocumentRequest(conversationId, TestUsers.RegularUserId, "sneaky.docx", [1]),
            TestContext.Current.CancellationToken));

        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        Assert.Equal(0, await ctx.ConversationDocuments
            .CountAsync(x => x.ConversationId == conversationId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// What a stopped turn leaves behind, end to end: the row is deactivated and the blob is gone, so
    /// the conversation never lists a file no message introduced.
    /// </summary>
    [Fact]
    public async Task DiscardAsync_AStoredDocument_LeavesNoLiveRowAndNoBlob()
    {
        var conversationId = await _fixture.AddConversationAsync(cancellationToken: TestContext.Current.CancellationToken);
        var stored = await StoreAsync(conversationId, "abandoned.docx", [1, 2, 3]);

        using var scope = _fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IGeneratedDocumentService>().DiscardAsync(stored.Id);

        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        var document = await ctx.ConversationDocuments
            .AsNoTracking()
            .SingleAsync(x => x.Id == stored.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(document.DateDeactivated);
        Assert.Empty(await _fixture.Factory.BlobStorage.DownloadAsync(
            GeneratedContainer, document.Path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The index the usage table deferred until an agent could write a non-null <c>ModelId</c>, which
    /// the per-user ceiling reads and which only exists if the migration actually applied.
    /// </summary>
    [Fact]
    public async Task Schema_ConversationUsageToolCall_CarriesTheModelAndDateIndex()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var indexes = await ctx.Database
            .SqlQuery<string>($"""
                SELECT i.name AS Value
                FROM sys.indexes i
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE s.name = 'Core' AND o.name = 'ConversationUsageToolCall'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains("IX_ConversationUsageToolCall_ModelId_DateCreated", indexes);
    }

    private async Task<MessageAttachmentDto> StoreAsync(Guid conversationId, string fileName, byte[] content)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IGeneratedDocumentService>();

        return await service.StoreAsync(
            new GeneratedDocumentRequest(conversationId, TestUsers.RegularUserId, fileName, content),
            TestContext.Current.CancellationToken);
    }
}
