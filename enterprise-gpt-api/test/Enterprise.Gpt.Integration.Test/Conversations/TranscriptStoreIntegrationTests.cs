using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Entity.Transcripts.Models;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Enterprise.Gpt.Repository.Cosmos.Models;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Conversations;

/// <summary>
/// Proves the storage foundation against a real Cosmos DB emulator. Transactional batch atomicity
/// and ordered cross-document queries are exactly what the in-memory fakes cannot prove, and the
/// cutover destroys every existing transcript, so this is what makes that step safe to take.
/// </summary>
[Collection(CosmosEmulatorCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TranscriptStoreIntegrationTests(CosmosEmulatorFixture fixture)
{
    private readonly CosmosEmulatorFixture _fixture = fixture;

    private static QueryDefinition MessageQuery(Guid conversationId)
    {
        return new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId ORDER BY c.dateCreated")
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId);
    }

    private async Task<(Guid UserId, Guid ConversationId, PartitionKey Partition)> SeedConversationAsync(
        int messageCount, CancellationToken cancellationToken)
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var partition = new PartitionKey(PartitionKeys.For(userId, conversationId));
        var createdAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        var header = TranscriptHeaderDocument.Create(userId, conversationId, "Emulator conversation", createdAt);
        await _fixture.Store.ExecuteBatchAsync(
            partition, batch => batch.Create(header.Id, header), cancellationToken);

        // Batched at the Cosmos ceiling, so the write path is exercised at its real limit.
        for (var written = 0; written < messageCount; written += TranscriptBatch.MaxOperations)
        {
            var take = Math.Min(TranscriptBatch.MaxOperations, messageCount - written);
            var offset = written;

            var result = await _fixture.Store.ExecuteBatchAsync(partition, batch =>
            {
                for (var index = 0; index < take; index++)
                {
                    var message = TranscriptMessageDocument.Create(
                        userId,
                        conversationId,
                        Guid.NewGuid(),
                        (offset + index) % 2 == 0 ? ChatRoles.User : ChatRoles.Assistant,
                        $"Message {offset + index}",
                        createdAt.AddSeconds(offset + index));

                    batch.Create(message.Id, message);
                }
            }, cancellationToken);

            Assert.True(result.IsSuccess, result.Describe());
        }

        return (userId, conversationId, partition);
    }

    /// <summary>
    /// The success criterion the whole project exists for: the largest document a conversation
    /// produces is bounded by its largest single message, not by how many messages it holds.
    /// </summary>
    [Fact]
    public async Task Write_FiveThousandMessages_KeepsEveryDocumentBoundedAndReadsThemInOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int MessageCount = 5000;

        var (_, conversationId, partition) = await SeedConversationAsync(MessageCount, cancellationToken);

        List<TranscriptMessageDocument> read = [];
        string? continuationToken = null;

        do
        {
            var page = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
                MessageQuery(conversationId), partition, 200, continuationToken, cancellationToken);

            read.AddRange(page.Items);
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        Assert.Equal(MessageCount, read.Count);

        // Ordered by dateCreated with no sequence field and no array position to fall back on.
        Assert.Equal(
            [.. read.Select(x => x.DateCreated).Order()],
            read.Select(x => x.DateCreated));
        Assert.Equal("Message 0", read[0].Content);
        Assert.Equal($"Message {MessageCount - 1}", read[^1].Content);

        // Every document is its own message plus a small envelope, so item size does not grow with
        // message count — which is what the previous shape could not say.
        var sizes = await ReadDocumentSizesAsync(conversationId, partition, cancellationToken);
        Assert.All(sizes, size => Assert.True(size < 2048, $"a message document was {size} bytes"));
    }

    private async Task<List<int>> ReadDocumentSizesAsync(
        Guid conversationId, PartitionKey partition, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT VALUE LENGTH(ToString(c)) FROM c WHERE c.type = @type AND c.conversationId = @conversationId")
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId);

        List<int> sizes = [];
        using var iterator = _fixture.Container.GetItemQueryIterator<int>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = partition });

        while (iterator.HasMoreResults)
        {
            sizes.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return sizes;
    }

    /// <summary>
    /// The atomicity the header's counters depend on: if the batch that creates a turn's messages
    /// can partially land, <c>messageCount</c> becomes advisory rather than exact.
    /// </summary>
    [Fact]
    public async Task ExecuteBatch_WhenTheSecondOperationViolatesAUniqueId_LeavesNothingBehind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (userId, conversationId, partition) = await SeedConversationAsync(0, cancellationToken);

        var existing = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.User, "Already here", DateTimeOffset.UtcNow);
        await _fixture.Store.ExecuteBatchAsync(
            partition, batch => batch.Create(existing.Id, existing), cancellationToken);

        var fresh = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.User, "Should not survive", DateTimeOffset.UtcNow);

        var result = await _fixture.Store.ExecuteBatchAsync(partition, batch => batch
            .Create(fresh.Id, fresh)
            .Create(existing.Id, existing), cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.FailedOperationIndex);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, result.FailedOperationStatusCode);

        // The first operation succeeded in isolation and must still have been rolled back.
        var orphan = await _fixture.Store.ReadItemAsync<TranscriptMessageDocument>(
            fresh.Id, partition, cancellationToken);
        Assert.Null(orphan);
    }

    [Fact]
    public async Task Query_WithoutAPartitionKey_ThrowsRatherThanScanningEveryPartition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            MessageQuery(Guid.NewGuid()), default, 10, null, cancellationToken));
    }

    /// <summary>
    /// The descending variant the paged read endpoint issues, with its <c>before</c> cursor.
    /// </summary>
    [Fact]
    public async Task Query_WithABeforeCursor_ReturnsOnlyEarlierMessagesNewestFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, conversationId, partition) = await SeedConversationAsync(20, cancellationToken);

        var all = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            MessageQuery(conversationId), partition, 100, null, cancellationToken);
        var cursor = all.Items[10].DateCreated;

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND (IS_NULL(@before) OR c.dateCreated < @before) ORDER BY c.dateCreated DESC")
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@before", cursor);

        var page = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            query, partition, 5, null, cancellationToken);

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, x => Assert.True(x.DateCreated < cursor));
        Assert.Equal(
            [.. page.Items.Select(x => x.DateCreated).OrderDescending()],
            page.Items.Select(x => x.DateCreated));
    }

    /// <summary>
    /// The same query with a null cursor must return everything, since the read path passes null
    /// for an unpaged request.
    /// </summary>
    [Fact]
    public async Task Query_WithANullBeforeCursor_ReturnsEveryMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, conversationId, partition) = await SeedConversationAsync(6, cancellationToken);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId AND (IS_NULL(@before) OR c.dateCreated < @before) ORDER BY c.dateCreated DESC")
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId)
            .WithParameter("@before", (DateTimeOffset?)null);

        var page = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            query, partition, 100, null, cancellationToken);

        Assert.Equal(6, page.Items.Count);
    }

    /// <summary>
    /// A foreign conversation id composes a partition key that addresses nothing, so a missed
    /// ownership check in code cannot expose another user's transcript.
    /// </summary>
    [Fact]
    public async Task Read_WithAnotherUsersPartitionKey_FindsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, conversationId, _) = await SeedConversationAsync(4, cancellationToken);

        var foreignPartition = new PartitionKey(PartitionKeys.For(Guid.NewGuid(), conversationId));

        var header = await _fixture.Store.ReadItemAsync<TranscriptHeaderDocument>(
            conversationId.ToString(), foreignPartition, cancellationToken);
        var messages = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            MessageQuery(conversationId), foreignPartition, 100, null, cancellationToken);

        Assert.Null(header);
        Assert.Empty(messages.Items);
    }

    /// <summary>
    /// The header patch a completed turn issues, applied for real rather than merely recorded.
    /// </summary>
    [Fact]
    public async Task ExecuteBatch_HeaderCounterPatch_IsAppliedAtomicallyWithTheMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (userId, conversationId, partition) = await SeedConversationAsync(0, cancellationToken);

        var user = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.User, "Question", DateTimeOffset.UtcNow) with
        { Tokens = 7 };
        var assistant = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant, "Answer", DateTimeOffset.UtcNow.AddTicks(1)) with
        { Tokens = 11 };

        var result = await _fixture.Store.ExecuteBatchAsync(partition, batch => batch
            .Create(user.Id, user)
            .Create(assistant.Id, assistant)
            .Patch(conversationId.ToString(),
            [
                PatchOperation.Increment("/messageCount", 2),
                PatchOperation.Increment("/contextTokens", 18),
            ]), cancellationToken);

        Assert.True(result.IsSuccess, result.Describe());

        var header = await _fixture.Store.ReadItemAsync<TranscriptHeaderDocument>(
            conversationId.ToString(), partition, cancellationToken);
        var messages = await _fixture.Store.QueryAsync<TranscriptMessageDocument>(
            MessageQuery(conversationId), partition, 100, null, cancellationToken);

        Assert.NotNull(header);
        Assert.Equal(2, header.MessageCount);
        Assert.Equal(18, header.ContextTokens);
        Assert.Equal(header.MessageCount, messages.Items.Count);
        Assert.Equal(header.ContextTokens, messages.Items.Sum(x => x.Tokens));
    }

    /// <summary>
    /// A document written by this application must round-trip through the configured serializer with
    /// its attribute-driven names, string enums and fixed-width timestamps intact.
    /// </summary>
    [Fact]
    public async Task Write_MessageDocument_RoundTripsEveryProperty()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (userId, conversationId, partition) = await SeedConversationAsync(0, cancellationToken);

        var written = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant, "The **answer**.",
            new DateTimeOffset(2026, 8, 15, 14, 30, 0, TimeSpan.FromHours(2))) with
        {
            Tokens = 12,
            TokenAccuracy = TokenAccuracies.Estimated,
            HtmlContent = "<p>The <strong>answer</strong>.</p>",
            Model = "gpt-test",
            Usage = new TranscriptMessageUsage { InputTokens = 100, OutputTokens = 50 }
        };

        await _fixture.Store.ExecuteBatchAsync(
            partition, batch => batch.Create(written.Id, written), cancellationToken);

        var read = await _fixture.Store.ReadItemAsync<TranscriptMessageDocument>(
            written.Id, partition, cancellationToken);

        Assert.NotNull(read);
        Assert.Equal(written.Content, read.Content);
        Assert.Equal(written.HtmlContent, read.HtmlContent);
        Assert.Equal(written.Tokens, read.Tokens);
        Assert.Equal(written.TokenAccuracy, read.TokenAccuracy);
        Assert.Equal(written.Model, read.Model);
        Assert.Equal(written.Role, read.Role);
        Assert.Equal(written.Usage!.InputTokens, read.Usage!.InputTokens);
        Assert.Equal(written.DateCreated.UtcTicks, read.DateCreated.UtcTicks);
        Assert.Equal(TranscriptDocumentTypes.Message, read.Type);
        Assert.Equal(PartitionKeys.For(userId, conversationId), read.Pk);
    }

    /// <summary>
    /// The feedback patch against real Cosmos JSON-patch semantics, which is the half the in-memory
    /// store deliberately cannot prove: it ignores patch operations. Set writes the block, Remove
    /// takes it away again, and neither disturbs the message.
    /// </summary>
    [Fact]
    public async Task PatchItem_Feedback_SetsThenRemovesTheBlockWithoutTouchingTheMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (userId, conversationId, partition) = await SeedConversationAsync(0, cancellationToken);
        var ratedAt = new DateTimeOffset(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

        var written = TranscriptMessageDocument.Create(
            userId, conversationId, Guid.NewGuid(), ChatRoles.Assistant, "The answer.",
            new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero));

        await _fixture.Store.ExecuteBatchAsync(
            partition, batch => batch.Create(written.Id, written), cancellationToken);

        var applied = await _fixture.Store.PatchItemAsync(
            written.Id, partition,
            [
                PatchOperation.Set(
                    "/feedback",
                    new TranscriptMessageFeedback { Rating = MessageFeedbackRatings.Up, DateModified = ratedAt })
            ],
            cancellationToken);

        Assert.True(applied);

        var rated = await _fixture.Store.ReadItemAsync<TranscriptMessageDocument>(
            written.Id, partition, cancellationToken);

        Assert.Equal(MessageFeedbackRatings.Up, rated?.Feedback?.Rating);
        Assert.Equal(ratedAt.UtcTicks, rated?.Feedback?.DateModified.UtcTicks);
        // The patch is surgical: the message it hangs off is untouched, which is the whole reason
        // the write path patches rather than replacing the document.
        Assert.Equal(written.Content, rated?.Content);
        Assert.Equal(written.DateCreated.UtcTicks, rated?.DateCreated.UtcTicks);

        // Set to null is how the service withdraws, and this is the assertion that says it works:
        // a JSON null deserializes back into a null Feedback, which the transcript projection
        // reports as no rating at all.
        await _fixture.Store.PatchItemAsync(
            written.Id, partition,
            [PatchOperation.Set<TranscriptMessageFeedback?>("/feedback", null)],
            cancellationToken);

        var withdrawn = await _fixture.Store.ReadItemAsync<TranscriptMessageDocument>(
            written.Id, partition, cancellationToken);

        Assert.Null(withdrawn?.Feedback);
        Assert.Equal(written.Content, withdrawn?.Content);

        // And withdrawing again is a no-op rather than a failure, which is what makes two racing
        // withdrawals safe. Set carries no precondition on the current value.
        var again = await _fixture.Store.PatchItemAsync(
            written.Id, partition,
            [PatchOperation.Set<TranscriptMessageFeedback?>("/feedback", null)],
            cancellationToken);

        Assert.True(again);
    }

    [Fact]
    public async Task ExecuteBatch_MoreOperationsThanCosmosAccepts_ThrowsBeforeLeavingTheProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (userId, conversationId, partition) = await SeedConversationAsync(0, cancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _fixture.Store.ExecuteBatchAsync(partition, batch =>
            {
                for (var index = 0; index <= TranscriptBatch.MaxOperations; index++)
                {
                    var message = TranscriptMessageDocument.Create(
                        userId, conversationId, Guid.NewGuid(), ChatRoles.User, "x", DateTimeOffset.UtcNow);

                    batch.Create(message.Id, message);
                }
            }, cancellationToken));
    }
}
