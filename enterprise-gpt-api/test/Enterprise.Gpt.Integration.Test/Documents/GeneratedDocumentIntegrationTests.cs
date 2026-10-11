using System.Net.Http.Json;
using System.Web;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Documents.Models;
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

    private readonly IntegrationTestFixture _fixture = fixture;

    public async ValueTask InitializeAsync() =>
        await _fixture.ResetConversationsAndDocumentsAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
