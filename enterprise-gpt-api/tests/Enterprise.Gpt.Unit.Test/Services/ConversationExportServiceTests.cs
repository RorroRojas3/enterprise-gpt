using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Transcripts;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using FluentValidation;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class ConversationExportServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ITranscriptStore _transcriptStore = Substitute.For<ITranscriptStore>();
    private readonly ConversationExportService _service;

    public ConversationExportServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new ConversationExportService(
            NullLogger<ConversationExportService>.Instance, _transcriptStore, _tokenService, _fixture.Context);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Conversation> AddConversationAsync(Guid? userId = null, DateTimeOffset? deactivated = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = "Planning notes",
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }

    private void SetUpTranscript(Conversation conversation, params TranscriptMessageDocument[] messages)
    {
        var partition = new PartitionKey(PartitionKeys.For(conversation.UserId, conversation.Id));
        var header = TranscriptHeaderDocument.Create(
            conversation.UserId, conversation.Id, conversation.Name, conversation.DateCreated) with
        {
            MessageCount = messages.Length
        };

        _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
                conversation.Id.ToString(), partition, Arg.Any<CancellationToken>())
            .Returns(header);
        _transcriptStore.QueryAsync<TranscriptMessageDocument>(
                Arg.Any<QueryDefinition>(), partition, Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TranscriptPage<TranscriptMessageDocument>(messages, null));
    }

    private static TranscriptMessageDocument Message(
        Conversation conversation, ChatRoles role, string content, string? html, int secondsIn)
    {
        return TranscriptMessageDocument.Create(
            conversation.UserId, conversation.Id, Guid.NewGuid(), role, content,
            conversation.DateCreated.AddSeconds(secondsIn)) with
        {
            HtmlContent = html
        };
    }

    [Fact]
    public async Task ExportConversationAsync_Html_AssemblesTheStoredHtmlInOrder()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.System, "System prompt.", "<p>System prompt.</p>", 0),
            Message(conversation, ChatRoles.User, "Question?", "<p>Question?</p>", 1),
            Message(conversation, ChatRoles.Assistant, "Answer.", "<p>Answer.</p>", 2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.StartsWith("text/html", export.ContentType, StringComparison.Ordinal);
        Assert.Contains("<p>Question?</p>", export.Content, StringComparison.Ordinal);
        Assert.Contains("<p>Answer.</p>", export.Content, StringComparison.Ordinal);
        Assert.True(
            export.Content.IndexOf("<p>Question?</p>", StringComparison.Ordinal)
            < export.Content.IndexOf("<p>Answer.</p>", StringComparison.Ordinal));

        // The assistant's standing instructions are not part of the conversation the user had.
        Assert.DoesNotContain("System prompt.", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_MessageWithNoStoredHtml_EscapesItsPlainText()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.User, "1 < 2 & <b>bold</b>", null, 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.Contains("1 &lt; 2 &amp; &lt;b&gt;bold&lt;/b&gt;", export.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bold</b>", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_ConversationWithNoMessages_ReturnsAValidDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.Contains("<!DOCTYPE html>", export.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{MESSAGES}}", export.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{DATE}}", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_Json_UsesTheStoredPropertyNames()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.Assistant, "Answer.", "<p>Answer.</p>", 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "json", TestContext.Current.CancellationToken);

        Assert.StartsWith("application/json", export.ContentType, StringComparison.Ordinal);
        foreach (var name in new[] { "\"role\"", "\"content\"", "\"htmlContent\"", "\"tokens\"", "\"tokenAccuracy\"", "\"dateCreated\"" })
        {
            Assert.Contains(name, export.Content, StringComparison.Ordinal);
        }

        // A string, matching the stored document rather than the integer the HTTP DTOs use.
        Assert.Contains("\"assistant\"", export.Content.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_NoFormat_DefaultsToHtml()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, null, TestContext.Current.CancellationToken);

        Assert.StartsWith("text/html", export.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_UnsupportedFormat_ThrowsValidationNamingTheSupportedOnes()
    {
        var conversation = await AddConversationAsync();

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.ExportConversationAsync(conversation.Id, "pdf", TestContext.Current.CancellationToken));

        Assert.Contains("html", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("format", Assert.Single(exception.Errors).PropertyName);
    }

    /// <summary>
    /// Exporting someone else's conversation is a 404, matching how every other conversation route
    /// treats a foreign id — a 403 would confirm the conversation exists.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_AnotherUsersConversation_ThrowsNotFound()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.User, "Private.", "<p>Private.</p>", 1));

        // The caller changes, not the owner: a conversation owned by a user that does not exist
        // would fail the foreign key rather than the ownership check.
        _tokenService.GetOid().Returns(Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.ExportConversationAsync(conversation.Id, "html", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportConversationAsync_DeactivatedConversation_ThrowsNotFound()
    {
        var conversation = await AddConversationAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.ExportConversationAsync(conversation.Id, "html", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportConversationAsync_ConversationWithNoTranscript_ThrowsNotFound()
    {
        var conversation = await AddConversationAsync();
        _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
                Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<CancellationToken>())
            .Returns((TranscriptHeaderDocument?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.ExportConversationAsync(conversation.Id, "html", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The conversation name is user-supplied and reaches the export's title, which is the one place
    /// it would otherwise land in markup unescaped.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_ConversationNameWithMarkup_IsEscapedIntoTheDocument()
    {
        var conversation = await AddConversationAsync();
        conversation.Name = "<script>alert(1)</script>";

        using (var ctx = _fixture.CreateContext())
        {
            ctx.Conversations.Update(conversation);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<script>alert(1)</script>", export.Content, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_FileName_IsDerivedFromTheConversationName()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.Equal("Planning-notes.html", export.FileName);
    }
}
