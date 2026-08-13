using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Cosmos;

/// <summary>
/// Proves the storage layer against a real Cosmos engine. Hierarchical partition keys,
/// transactional batch atomicity and ordered cross-document queries are precisely the behaviours
/// an in-memory fake asserts rather than demonstrates, which is why these run on the emulator.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CosmosTranscriptStorageTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    private IAzureCosmosService Service => _fixture.Factory.Services.GetRequiredService<IAzureCosmosService>();

    private CosmosClient Client => _fixture.Factory.Services.GetRequiredService<CosmosClient>();

    private CosmosOptions Options => _fixture.Factory.Services.GetRequiredService<IOptions<CosmosOptions>>().Value;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetTranscriptsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TestMessage CreateMessage(Guid userId, Guid conversationId, long sequence, string content = "hello")
    {
        return new TestMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            ConversationId = conversationId,
            Type = "message",
            Sequence = sequence,
            Body = content
        };
    }

    private async Task SeedMessagesAsync(Guid userId, Guid conversationId, int count, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < count; offset += 100)
        {
            var batch = Enumerable
                .Range(offset, Math.Min(100, count - offset))
                .Select(sequence => CosmosBatchOperation.CreateItem(CreateMessage(userId, conversationId, sequence)))
                .ToList();

            await Service.ExecuteBatchAsync(userId, conversationId, batch, cancellationToken);
        }
    }

    [Fact]
    public async Task ExecuteBatchAsync_MixedOperations_CommitsThemTogether()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var header = new TestHeader
        {
            Id = conversationId.ToString(),
            UserId = userId,
            ConversationId = conversationId,
            Type = "conversation",
            MessageCount = 0
        };

        await Service.ExecuteBatchAsync(userId, conversationId,
        [
            CosmosBatchOperation.CreateItem(header),
            CosmosBatchOperation.CreateItem(CreateMessage(userId, conversationId, 0)),
            CosmosBatchOperation.PatchItem(conversationId.ToString(), [PatchOperation.Increment("/messageCount", 1)])
        ], TestContext.Current.CancellationToken);

        var stored = await Service.GetItemAsync<TestHeader>(conversationId.ToString(), userId, conversationId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.MessageCount);
    }

    /// <summary>
    /// A batch is all-or-nothing: the first operation must not survive the second one failing.
    /// </summary>
    [Fact]
    public async Task ExecuteBatchAsync_SecondOperationConflicts_WritesNothingAndNamesTheOperation()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var duplicate = CreateMessage(userId, conversationId, 1);
        var survivor = CreateMessage(userId, conversationId, 0);

        await Service.ExecuteBatchAsync(userId, conversationId, [CosmosBatchOperation.CreateItem(duplicate)], TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<CosmosBatchFailedException>(() =>
            Service.ExecuteBatchAsync(userId, conversationId,
            [
                CosmosBatchOperation.CreateItem(survivor),
                CosmosBatchOperation.CreateItem(duplicate)
            ], TestContext.Current.CancellationToken));

        Assert.Equal(1, exception.FailedOperationIndex);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);

        var rolledBack = await Service.GetItemAsync<TestMessage>(survivor.Id, userId, conversationId, TestContext.Current.CancellationToken);
        Assert.Null(rolledBack);
    }

    [Fact]
    public async Task QueryPageAsync_MoreMatchesThanPageSize_PagesInQueryOrder()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedMessagesAsync(userId, conversationId, 250, TestContext.Current.CancellationToken);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'message' ORDER BY c.sequence");
        var sequences = new List<long>();
        string? continuationToken = null;
        var requests = 0;

        do
        {
            var page = await Service.QueryPageAsync<TestMessage>(query, userId, conversationId, 100, continuationToken, TestContext.Current.CancellationToken);
            sequences.AddRange(page.Items.Select(message => message.Sequence));
            continuationToken = page.ContinuationToken;
            requests++;
        }
        while (continuationToken is not null);

        // Not an exact request count: MaxItemCount is a maximum, and a continuation token is null
        // only once the backend knows the set is exhausted, so the same 250 items can legitimately
        // drain in a different number of round trips.
        Assert.True(requests > 1, $"Expected the page size to force more than one request; got {requests}.");
        Assert.Equal(250, sequences.Count);
        Assert.Equal(Enumerable.Range(0, 250).Select(value => (long)value), sequences);
    }

    /// <summary>
    /// The headline storage property: with one document per message, the largest item a
    /// conversation produces is bounded by its largest single message rather than by its length.
    /// </summary>
    [Fact]
    public async Task Transcript_FiveThousandMessages_KeepsEveryDocumentBoundedByItsOwnContent()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedMessagesAsync(userId, conversationId, 5_000, TestContext.Current.CancellationToken);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'message' ORDER BY c.sequence");
        var count = 0;
        var largestDocument = 0;
        string? continuationToken = null;

        do
        {
            var page = await Service.QueryPageAsync<TestMessage>(query, userId, conversationId, 500, continuationToken, TestContext.Current.CancellationToken);
            foreach (var message in page.Items)
            {
                Assert.Equal(count, message.Sequence);
                largestDocument = Math.Max(largestDocument, JsonSerializer.Serialize(message).Length);
                count++;
            }

            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        Assert.Equal(5_000, count);
        Assert.True(largestDocument < 2_048, $"Largest document was {largestDocument} bytes; message size must not grow with transcript length.");
    }

    [Fact]
    public async Task PurgeConversationAsync_OneConversation_LeavesTheUsersOtherConversationIntact()
    {
        var userId = Guid.NewGuid();
        var purged = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await SeedMessagesAsync(userId, purged, 120, TestContext.Current.CancellationToken);
        await SeedMessagesAsync(userId, kept, 30, TestContext.Current.CancellationToken);

        var deleted = await Service.PurgeConversationAsync(userId, purged, TestContext.Current.CancellationToken);

        Assert.Equal(120, deleted);

        var query = new QueryDefinition("SELECT * FROM c");
        var remaining = await Service.QueryPageAsync<TestMessage>(query, userId, purged, 100, null, TestContext.Current.CancellationToken);
        Assert.Empty(remaining.Items);

        var survivors = await Service.QueryPageAsync<TestMessage>(query, userId, kept, 100, null, TestContext.Current.CancellationToken);
        Assert.Equal(30, survivors.Items.Count);
    }

    /// <summary>
    /// The naming contract is the annotations on the document type, not the member names — the
    /// mismatch this replaced once made a hand-written query match nothing.
    /// </summary>
    [Fact]
    public async Task Serialization_AnnotatedProperty_IsStoredUnderTheAnnotatedNameAndQueryable()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(userId, conversationId, 0, "annotated");
        await Service.ExecuteBatchAsync(userId, conversationId, [CosmosBatchOperation.CreateItem(message)], TestContext.Current.CancellationToken);

        var query = new QueryDefinition("SELECT * FROM c WHERE c.conversationId = @conversationId AND c.type = 'message'")
            .WithParameter("@conversationId", conversationId);

        var page = await Service.QueryPageAsync<TestMessage>(query, userId, conversationId, 10, null, TestContext.Current.CancellationToken);

        Assert.Single(page.Items);
        Assert.Equal("annotated", page.Items[0].Body);
    }

    [Fact]
    public async Task EnsureTranscriptContainerAsync_ContainerWithDifferentPartitionKeyPaths_FailsNamingBothPaths()
    {
        var database = Client.GetDatabase(Options.DatabaseId);
        var containerId = $"mismatched-{Guid.NewGuid():N}";
        await database.CreateContainerAsync(new ContainerProperties(containerId, "/userId"), cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var options = new CosmosOptions
            {
                ConnectionString = Options.ConnectionString,
                DatabaseId = Options.DatabaseId,
                TranscriptContainerId = containerId
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CosmosContainerBootstrap.EnsureTranscriptContainerAsync(Client, options, NullLogger.Instance, TestContext.Current.CancellationToken));

            Assert.Contains("/userId", exception.Message);
            Assert.Contains("/conversationId", exception.Message);
        }
        finally
        {
            await database.GetContainer(containerId).DeleteContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private sealed class TestHeader
    {
        [JsonPropertyName("id")] public string Id { get; set; } = null!;

        [JsonPropertyName("userId")] public Guid UserId { get; set; }

        [JsonPropertyName("conversationId")] public Guid ConversationId { get; set; }

        [JsonPropertyName("type")] public string Type { get; set; } = null!;

        [JsonPropertyName("messageCount")] public long MessageCount { get; set; }
    }

    private sealed class TestMessage
    {
        [JsonPropertyName("id")] public string Id { get; set; } = null!;

        [JsonPropertyName("userId")] public Guid UserId { get; set; }

        [JsonPropertyName("conversationId")] public Guid ConversationId { get; set; }

        [JsonPropertyName("type")] public string Type { get; set; } = null!;

        [JsonPropertyName("sequence")] public long Sequence { get; set; }

        // Annotated to a name the camel-case policy would never produce from this member, so a
        // round trip proves the annotation is the contract rather than merely agreeing with it.
        [JsonPropertyName("content")] public string Body { get; set; } = null!;
    }
}
