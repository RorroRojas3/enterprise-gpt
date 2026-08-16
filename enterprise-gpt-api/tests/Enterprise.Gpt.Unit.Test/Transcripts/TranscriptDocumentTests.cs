using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Serialization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Transcripts;

public sealed class TranscriptDocumentTests
{
    private static readonly JsonSerializerOptions Options = CosmosSerialization.CreateOptions();

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConversationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void For_ComposesBothIdentifiers()
    {
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            PartitionKeys.For(UserId, ConversationId));
    }

    [Fact]
    public void For_DifferentConversationsOfOneUser_ProduceDifferentPartitions()
    {
        Assert.NotEqual(PartitionKeys.For(UserId, ConversationId), PartitionKeys.For(UserId, Guid.NewGuid()));
    }

    /// <summary>
    /// The tenancy guarantee this design puts in storage: a foreign conversation id addresses a
    /// partition that does not exist rather than one a caller has to be filtered out of.
    /// </summary>
    [Fact]
    public void For_SameConversationIdUnderAnotherUser_AddressesADifferentPartition()
    {
        Assert.NotEqual(PartitionKeys.For(UserId, ConversationId), PartitionKeys.For(Guid.NewGuid(), ConversationId));
    }

    [Fact]
    public void HeaderCreate_ComposesThePartitionKeyFromTheHelper()
    {
        var header = TranscriptHeaderDocument.Create(UserId, ConversationId, "Name", DateTimeOffset.UtcNow);

        Assert.Equal(PartitionKeys.For(UserId, ConversationId), header.Pk);
        Assert.Equal(ConversationId.ToString(), header.Id);
    }

    [Fact]
    public void MessageCreate_ComposesThePartitionKeyFromTheHelper()
    {
        var messageId = Guid.NewGuid();

        var message = TranscriptMessageDocument.Create(
            UserId, ConversationId, messageId, ChatRoles.User, "Hello", DateTimeOffset.UtcNow);

        Assert.Equal(PartitionKeys.For(UserId, ConversationId), message.Pk);
        Assert.Equal(messageId.ToString(), message.Id);
    }

    [Fact]
    public void Header_Serialized_CarriesTheDocumentedProperties()
    {
        var header = TranscriptHeaderDocument.Create(UserId, ConversationId, "Name", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(header, Options));

        foreach (var name in new[]
        {
            "id", "pk", "userId", "conversationId", "type", "name", "contextTokens",
            "messageCount", "schemaVersion", "dateCreated", "dateModified", "dateDeactivated"
        })
        {
            Assert.True(document.RootElement.TryGetProperty(name, out _), $"header is missing '{name}'");
        }

        Assert.Equal("conversation", document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Message_Serialized_CarriesTheDocumentedProperties()
    {
        var message = TranscriptMessageDocument.Create(
            UserId, ConversationId, Guid.NewGuid(), ChatRoles.Assistant, "Hi", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, Options));

        foreach (var name in new[]
        {
            "id", "pk", "userId", "conversationId", "type", "role", "content", "htmlContent",
            "tokens", "tokenAccuracy", "model", "usage", "dateCreated"
        })
        {
            Assert.True(document.RootElement.TryGetProperty(name, out _), $"message is missing '{name}'");
        }

        Assert.Equal("message", document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Message_Serialized_CarriesNoSequenceField()
    {
        // Ordering is dateCreated alone. A sequence field would need a writer, and the only correct
        // writer is a lock or an allocator this design deliberately does not have.
        var message = TranscriptMessageDocument.Create(
            UserId, ConversationId, Guid.NewGuid(), ChatRoles.User, "Hello", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, Options));

        Assert.DoesNotContain(
            document.RootElement.EnumerateObject(),
            property => property.Name.Contains("sequence", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ordinal", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Message_AssistantRole_SerializesAsAString()
    {
        var message = TranscriptMessageDocument.Create(
            UserId, ConversationId, Guid.NewGuid(), ChatRoles.Assistant, "Hi", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, Options));

        // Camel-cased, matching the SSE wire contract's role values rather than the enum's declared
        // casing — the naming policy has to be given to the converter to get this.
        Assert.Equal("assistant", document.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public void Message_TokenAccuracy_DefaultsToEstimatedAndSerializesAsAString()
    {
        var message = TranscriptMessageDocument.Create(
            UserId, ConversationId, Guid.NewGuid(), ChatRoles.User, "Hello", DateTimeOffset.UtcNow);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message, Options));

        Assert.Equal(TokenAccuracies.Estimated, message.TokenAccuracy);

        // Camel-cased in storage. The HTTP DTO carries its own converter and serves "Estimated"
        // there, because the two contracts are separate and the API's is already established.
        Assert.Equal("estimated", document.RootElement.GetProperty("tokenAccuracy").GetString());
    }

    [Fact]
    public void Header_Serialized_StampsTheCurrentSchemaVersion()
    {
        var header = TranscriptHeaderDocument.Create(UserId, ConversationId, "Name", DateTimeOffset.UtcNow);

        Assert.Equal(TranscriptHeaderDocument.CurrentSchemaVersion, header.SchemaVersion);
    }

    [Fact]
    public void Documents_RoundTripThroughTheCosmosSerializer()
    {
        var header = TranscriptHeaderDocument.Create(UserId, ConversationId, "Name", DateTimeOffset.UtcNow);
        header.MessageCount = 4;
        header.ContextTokens = 1234;

        var restored = JsonSerializer.Deserialize<TranscriptHeaderDocument>(
            JsonSerializer.Serialize(header, Options), Options);

        Assert.NotNull(restored);
        Assert.Equal(header.Pk, restored.Pk);
        Assert.Equal(header.UserId, restored.UserId);
        Assert.Equal(header.ConversationId, restored.ConversationId);
        Assert.Equal(4, restored.MessageCount);
        Assert.Equal(1234, restored.ContextTokens);
        Assert.Equal(TranscriptDocumentTypes.Conversation, restored.Type);
    }
}
