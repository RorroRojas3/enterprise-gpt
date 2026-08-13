using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using FluentValidation;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

public sealed class ConversationExportServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IAzureCosmosService _cosmosService = Substitute.For<IAzureCosmosService>();
    private readonly ConversationExportService _service;

    public ConversationExportServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new ConversationExportService(
            NullLogger<ConversationExportService>.Instance,
            _tokenService,
            _cosmosService,
            _fixture.Context);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Conversation> AddConversationAsync(Guid? userId = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = "Planning",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }

    private void SetUpTranscript(Guid conversationId, params CosmosConversationMessage[] messages)
    {
        var date = DateTimeOffset.UtcNow;
        _cosmosService.GetItemAsync<CosmosConversationHeader>(
                conversationId.ToString(), KnownIds.SeedUserId, conversationId, Arg.Any<CancellationToken>())
            .Returns(new CosmosConversationHeader
            {
                Id = conversationId,
                UserId = KnownIds.SeedUserId,
                ConversationId = conversationId,
                Name = "Planning",
                DateCreated = date,
                DateModified = date
            });

        _cosmosService.QueryPageAsync<CosmosConversationMessage>(
                Arg.Any<QueryDefinition>(), KnownIds.SeedUserId, conversationId, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new CosmosPage<CosmosConversationMessage>(messages, null));
    }

    private static CosmosConversationMessage BuildMessage(
        ChatRoles role, string content, long sequence, string? htmlContent = null)
    {
        return new CosmosConversationMessage
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            Role = MappingService.MapToRoleName(role),
            Content = content,
            HtmlContent = htmlContent,
            Tokens = 5,
            TokenAccuracy = TokenAccuracies.Estimated,
            Model = "gpt-test",
            DateCreated = DateTimeOffset.UtcNow
        };
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pdf")]
    public async Task ExportAsync_UnsupportedFormat_ThrowsValidationExceptionNamingTheSupportedOnes(string? format)
    {
        var conversation = await AddConversationAsync();

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.ExportAsync(conversation.Id, format, TestContext.Current.CancellationToken));

        Assert.Contains("html", exception.Message);
        Assert.Contains("json", exception.Message);
    }

    /// <summary>
    /// A foreign conversation is a 404 rather than a 403, matching how every other conversation
    /// route treats an id it does not own.
    /// </summary>
    [Fact]
    public async Task ExportAsync_AnotherUsersConversation_ThrowsNotFound()
    {
        var conversation = await AddConversationAsync();
        _tokenService.GetOid().Returns(Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.ExportAsync(conversation.Id, "html", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportAsync_Html_EmbedsTheStoredRenderingAndTitlesTheDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation.Id,
            BuildMessage(ChatRoles.User, "Hello", 1, "<p>Hello</p>"),
            BuildMessage(ChatRoles.Assistant, "Hi there", 2, "<p>Hi there</p>"));

        var file = await _service.ExportAsync(conversation.Id, "html", TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(file.Content);

        Assert.Equal("text/html; charset=utf-8", file.ContentType);
        Assert.Contains("<title>Planning</title>", html);
        Assert.Contains("<p>Hello</p>", html);
        Assert.Contains("<p>Hi there</p>", html);
    }

    /// <summary>
    /// A message stored before rendering existed is escaped into the output rather than omitted:
    /// dropping it would silently shorten the export.
    /// </summary>
    [Fact]
    public async Task ExportAsync_MessageWithoutStoredHtml_EscapesItsTextInstead()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation.Id, BuildMessage(ChatRoles.User, "5 < 6 & 7 > 6", 1));

        var file = await _service.ExportAsync(conversation.Id, "html", TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(file.Content);

        Assert.Contains("5 &lt; 6 &amp; 7 &gt; 6", html);
    }

    [Fact]
    public async Task ExportAsync_ConversationWithNoMessages_ReturnsAValidEmptyDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation.Id);

        var file = await _service.ExportAsync(conversation.Id, "html", TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(file.Content);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<main>", html);
        Assert.Contains("</html>", html);
    }

    /// <summary>
    /// The JSON export is the storage shape, so its property names are the stored document's.
    /// </summary>
    [Fact]
    public async Task ExportAsync_Json_UsesTheStoredDocumentsPropertyNames()
    {
        var conversation = await AddConversationAsync();
        var assistant = BuildMessage(ChatRoles.Assistant, "Hi there", 2, "<p>Hi there</p>");
        assistant.Usage = new CosmosConversationUsage { InputTokens = 11, OutputTokens = 7 };
        SetUpTranscript(conversation.Id, BuildMessage(ChatRoles.User, "Hello", 1), assistant);

        var file = await _service.ExportAsync(conversation.Id, "json", TestContext.Current.CancellationToken);

        Assert.Equal("application/json; charset=utf-8", file.ContentType);

        using var document = JsonDocument.Parse(file.Content);
        var root = document.RootElement;
        Assert.Equal("Planning", root.GetProperty("name").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        var first = messages[0];
        Assert.Equal("user", first.GetProperty("role").GetString());
        Assert.Equal("Hello", first.GetProperty("content").GetString());
        Assert.Equal("Estimated", first.GetProperty("tokenAccuracy").GetString());
        Assert.False(first.TryGetProperty("usage", out _));

        var second = messages[1];
        var usage = second.GetProperty("usage");
        Assert.Equal(11, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(7, usage.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task ExportAsync_ConversationName_BecomesTheDownloadFileName()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation.Id);

        var file = await _service.ExportAsync(conversation.Id, "json", TestContext.Current.CancellationToken);

        Assert.Equal("Planning.json", file.FileName);
    }
}
