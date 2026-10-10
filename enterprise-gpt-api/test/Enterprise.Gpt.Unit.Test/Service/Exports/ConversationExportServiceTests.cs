using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Renderers;
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
    private static readonly DateTimeOffset ExportedAt = new(2026, 8, 19, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ITranscriptStore _transcriptStore = Substitute.For<ITranscriptStore>();
    private readonly ConversationExportService _service;

    public ConversationExportServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = CreateService(
            new HtmlExportRenderer(new MarkdownBlockMapper()),
            new JsonExportRenderer(),
            new MarkdownExportRenderer(),
            new WordExportRenderer(new MarkdownBlockMapper()));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private ConversationExportService CreateService(params IConversationExportRenderer[] renderers)
    {
        return new ConversationExportService(
            NullLogger<ConversationExportService>.Instance,
            _transcriptStore,
            _tokenService,
            _fixture.Context,
            renderers,
            new MutableTimeProvider { UtcNow = ExportedAt });
    }

    private async Task<Conversation> AddConversationAsync(
        Guid? userId = null, DateTimeOffset? deactivated = null, string name = "Planning notes")
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = name,
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

    private static string Text(ConversationExport export) => Encoding.UTF8.GetString(export.Content);

    [Fact]
    public async Task ExportConversationAsync_Html_RendersEachMessageUnderItsLabelInOrder()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.System, "System prompt.", "<p>System prompt.</p>", 0),
            Message(conversation, ChatRoles.User, "Question?", "<p>Question?</p>", 1),
            Message(conversation, ChatRoles.Assistant, "Answer.", "<p>Answer.</p>", 2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.StartsWith("text/html", export.ContentType, StringComparison.Ordinal);
        Assert.Contains("message--user", content, StringComparison.Ordinal);
        Assert.Contains("message--assistant", content, StringComparison.Ordinal);
        Assert.Contains("<p>Question?</p>", content, StringComparison.Ordinal);
        Assert.Contains("<p>Answer.</p>", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf("<p>Question?</p>", StringComparison.Ordinal)
            < content.IndexOf("<p>Answer.</p>", StringComparison.Ordinal));

        // The assistant's standing instructions are not part of the conversation the user had.
        Assert.DoesNotContain("System prompt.", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored rendering is not read at all any more, so a message written before server-side
    /// rendering existed has to export exactly as one written after it.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_Html_IgnoresTheStoredRendering()
    {
        var withHtml = await AddConversationAsync();
        SetUpTranscript(withHtml, Message(withHtml, ChatRoles.User, "**Loud** and clear.", "<p>ignored</p>", 1));

        var withoutHtml = await AddConversationAsync();
        SetUpTranscript(withoutHtml, Message(withoutHtml, ChatRoles.User, "**Loud** and clear.", null, 1));

        var first = Text(await _service.ExportConversationAsync(
            withHtml.Id, "html", TestContext.Current.CancellationToken));
        var second = Text(await _service.ExportConversationAsync(
            withoutHtml.Id, "html", TestContext.Current.CancellationToken));

        Assert.Contains("<strong>Loud</strong> and clear.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ExportConversationAsync_Html_EscapesTextThatLooksLikeMarkup()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.User, "1 < 2 & <b>bold</b>", null, 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.Contains("1 &lt; 2 &amp; &lt;b&gt;bold&lt;/b&gt;", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bold</b>", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Substitution is one pass, so a value spliced in is never scanned again.
    /// </summary>
    /// <remarks>
    /// HTML-encoding leaves a placeholder untouched — it holds no encodable character — so a name
    /// like this reaching a second <c>Replace</c> would splice the whole transcript into the
    /// document's own title.
    /// </remarks>
    [Theory]
    [InlineData("{{MESSAGES}}")]
    [InlineData("{{DATE}}")]
    [InlineData("{{THEME}}")]
    public async Task ExportConversationAsync_NameLooksLikeAPlaceholder_IsNotSubstituted(string name)
    {
        var conversation = await AddConversationAsync(name: name);
        SetUpTranscript(conversation, Message(conversation, ChatRoles.User, "Question?", null, 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.Contains($"<title>{name}</title>", content, StringComparison.Ordinal);
        Assert.Equal(1, content.Split("Question?").Length - 1);
    }

    /// <summary>
    /// Every value the template resolves comes from <see cref="ExportTheme"/>, and the block that
    /// supplies them is spliced in like any other placeholder.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_Html_ResolvesTheThemePlaceholder()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.User, "Question?", null, 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.DoesNotContain("{{THEME}}", content, StringComparison.Ordinal);
        Assert.Contains($"--brand: #{ExportTheme.Colors.Brand};", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_ConversationWithNoMessages_ReturnsAValidDocument()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.Contains("<!DOCTYPE html>", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{MESSAGES}}", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{DATE}}", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_Json_UsesTheStoredPropertyNames()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation, Message(conversation, ChatRoles.Assistant, "Answer.", "<p>Answer.</p>", 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "json", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.StartsWith("application/json", export.ContentType, StringComparison.Ordinal);
        foreach (var name in new[] { "\"role\"", "\"content\"", "\"htmlContent\"", "\"tokens\"", "\"tokenAccuracy\"", "\"dateCreated\"" })
        {
            Assert.Contains(name, content, StringComparison.Ordinal);
        }

        // A string, matching the stored document rather than the integer the HTTP DTOs use.
        Assert.Contains("\"assistant\"", content.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_Markdown_WritesEachMessageVerbatimUnderItsLabel()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.System, "System prompt.", null, 0),
            Message(conversation, ChatRoles.User, "What does `retrieval` do?", null, 1),
            Message(conversation, ChatRoles.Assistant, "It runs a **tool call**.", null, 2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "md", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.StartsWith("text/markdown", export.ContentType, StringComparison.Ordinal);
        Assert.Equal("Planning-notes.md", export.FileName);
        Assert.Contains("# Planning notes", content, StringComparison.Ordinal);
        Assert.Contains("## You", content, StringComparison.Ordinal);
        Assert.Contains("## Assistant", content, StringComparison.Ordinal);

        // Verbatim: a round trip through the parser would rewrite the fence and the emphasis.
        Assert.Contains("What does `retrieval` do?", content, StringComparison.Ordinal);
        Assert.Contains("It runs a **tool call**.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("System prompt.", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf("## You", StringComparison.Ordinal)
            < content.IndexOf("## Assistant", StringComparison.Ordinal));
    }

    /// <summary>
    /// A name carrying a newline would end the heading and turn the rest of it into body text.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_MarkdownWithAMultiLineName_KeepsTheTitleOnOneLine()
    {
        var conversation = await AddConversationAsync();
        conversation.Name = "Release\nplan";

        using (var ctx = _fixture.CreateContext())
        {
            ctx.Conversations.Update(conversation);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, "md", TestContext.Current.CancellationToken);

        Assert.Contains("# Release plan", Text(export), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConversationAsync_Docx_ReturnsAReadableWordPackage()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.User, "# Heading\n\nWhat about **this**?", null, 1),
            Message(conversation, ChatRoles.Assistant, "- one\n- two", null, 2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "docx", TestContext.Current.CancellationToken);

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", export.ContentType);
        Assert.Equal("Planning-notes.docx", export.FileName);

        var text = WordDocumentText(export.Content);
        Assert.Contains("Planning notes", text, StringComparison.Ordinal);
        Assert.Contains("You", text, StringComparison.Ordinal);
        Assert.Contains("Assistant", text, StringComparison.Ordinal);
        Assert.Contains("Heading", text, StringComparison.Ordinal);
        Assert.Contains("this", text, StringComparison.Ordinal);
        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.Contains("two", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading the package back proves it is a zip with the right parts; this proves Word will accept
    /// it. The styles and the numbering definitions are hand-built, and the schema is strict about the
    /// order of their children — a mistake there produces a file that opens here and that Word refuses.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_Docx_IsSchemaValid()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(
                conversation,
                ChatRoles.User,
                """
                # Heading

                Prose with **bold** and `code`, and a table inside the prompt, whose cells
                carry the band's own shading.

                | need | priority |
                | --- | --- |
                | one | high |
                """,
                null,
                1),
            Message(
                conversation,
                ChatRoles.Assistant,
                """
                - bullet
                  - nested

                1. first
                2. second

                > quoted

                ```csharp
                var x = 1;
                ```

                | a | b |
                | --- | --- |
                | 1 | 2 |

                > | quoted | table |
                > | --- | --- |
                > | 3 | 4 |

                ---

                A [link](https://example.test/a) and a trailing line.
                """,
                null,
                2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "docx", TestContext.Current.CancellationToken);

        using var stream = new MemoryStream(export.Content);
        using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, isEditable: false);

        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(word).ToList();

        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(error => $"{error.Path?.XPath}: {error.Description}")));
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

    [Theory]
    [InlineData("HTML")]
    [InlineData("Md")]
    [InlineData(" docx ")]
    public async Task ExportConversationAsync_FormatCasingAndPadding_IsAccepted(string format)
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var export = await _service.ExportConversationAsync(
            conversation.Id, format, TestContext.Current.CancellationToken);

        Assert.NotEmpty(export.Content);
    }

    [Fact]
    public async Task ExportConversationAsync_UnsupportedFormat_ThrowsValidationNamingTheSupportedOnes()
    {
        var conversation = await AddConversationAsync();

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.ExportConversationAsync(conversation.Id, "rtf", TestContext.Current.CancellationToken));

        foreach (var supported in ConversationExportFormatNames.Supported)
        {
            Assert.Contains(supported, exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("format", Assert.Single(exception.Errors).PropertyName);
    }

    /// <summary>
    /// The format is one this API supports, but the deployment registered no renderer for it — a PDF
    /// with no usable font, or a format withdrawn by <c>Export:DisabledFormats</c>. It must not
    /// collapse into the validation 400, which would tell a client to change a request that is
    /// correct, nor into a 500, which would tell it to retry.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_SupportedFormatWithNoRenderer_ThrowsExportRendererNotConfigured()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);

        var exception = await Assert.ThrowsAsync<ExportRendererNotConfiguredException>(
            () => _service.ExportConversationAsync(conversation.Id, "pdf", TestContext.Current.CancellationToken));

        Assert.Equal("pdf", exception.Format);
    }

    /// <summary>
    /// Ownership is checked after the format, so a caller sending a bad format learns that rather
    /// than being told the conversation does not exist — but a renderer that is merely unregistered
    /// must not leak the existence of someone else's conversation.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_UnregisteredFormat_ThrowsBeforeReadingTheTranscript()
    {
        var conversation = await AddConversationAsync();

        await Assert.ThrowsAsync<ExportRendererNotConfiguredException>(
            () => _service.ExportConversationAsync(conversation.Id, "pdf", TestContext.Current.CancellationToken));

        await _transcriptStore.DidNotReceive().ReadItemAsync<TranscriptHeaderDocument>(
            Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The registry is documented as last-wins so a deployment can override one format's renderer
    /// without removing the original. `ToDictionary` would instead throw on the duplicate — inside a
    /// scoped service's constructor, on every export request, surfacing as a 400 with a LINQ message
    /// in the body.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_TwoRenderersForOneFormat_UsesTheLastRegistered()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(conversation);
        var service = CreateService(new MarkdownExportRenderer(), new StubMarkdownRenderer());

        var export = await service.ExportConversationAsync(
            conversation.Id, "md", TestContext.Current.CancellationToken);

        Assert.Equal("stub.md", export.FileName);
    }

    /// <summary>
    /// A Tool document is a model-to-model artefact, not a prompt or an answer, and the label a
    /// message gets is a two-way choice — so without an allowlist it would be exported as an assistant
    /// answer that never existed.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_ToolMessage_IsExcludedLikeASystemMessage()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.Tool, "{\"tool\":\"result\"}", null, 1),
            Message(conversation, ChatRoles.Assistant, "The answer.", null, 2));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "md", TestContext.Current.CancellationToken);
        var content = Text(export);

        Assert.DoesNotContain("tool", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The answer.", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The template's placeholders are substituted before the transcript is spliced in. Doing it the
    /// other way round leaves the title and date replacements running over message text, which a
    /// model chooses — so an answer mentioning the placeholder had it rewritten silently.
    /// </summary>
    [Fact]
    public async Task ExportConversationAsync_MessageContainingATemplatePlaceholder_IsLeftAlone()
    {
        var conversation = await AddConversationAsync();
        SetUpTranscript(
            conversation,
            Message(conversation, ChatRoles.Assistant, "The token is {{DATE}} in the template.", null, 1));

        var export = await _service.ExportConversationAsync(
            conversation.Id, "html", TestContext.Current.CancellationToken);

        Assert.Contains("The token is {{DATE}} in the template.", Text(export), StringComparison.Ordinal);
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
        var content = Text(export);

        Assert.DoesNotContain("<script>alert(1)</script>", content, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", content, StringComparison.Ordinal);
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

    /// <summary>
    /// A second renderer claiming <see cref="ConversationExportFormats.Markdown"/>, so the registry's
    /// duplicate-key behaviour is observable.
    /// </summary>
    private sealed class StubMarkdownRenderer : IConversationExportRenderer
    {
        public ConversationExportFormats Format => ConversationExportFormats.Markdown;

        public ConversationExport Render(ConversationExportDocument document) =>
            new("stub"u8.ToArray(), "text/markdown; charset=utf-8", "stub.md");
    }

    /// <summary>
    /// Reads the produced package back with the OpenXML SDK rather than asserting on bytes, so the
    /// test fails on a document Word could not open rather than on one that merely changed.
    /// </summary>
    private static string WordDocumentText(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, isEditable: false);

        return word.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }
}
