using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts.Models;
using Enterprise.Gpt.Service.Exports;
using Enterprise.Gpt.Service.Exports.Models;
using Enterprise.Gpt.Service.Exports.Renderers;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Exports.Renderers;

/// <remarks>
/// <c>GlobalFontSettings.FontResolver</c> is process-global and set-once, so the fixture installs one shared
/// resolver. Glyphs depend on the host's fonts, so only structure is asserted here.
/// </remarks>
[Collection(PdfFontCollection.Name)]
public sealed class PdfExportRendererTests(PdfFontFixture fonts)
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 8, 19, 9, 30, 0, TimeSpan.Zero);

    private readonly PdfFontFixture _fonts = fonts;
    private readonly PdfExportRenderer _renderer = new(new MarkdownBlockMapper());

    private static ConversationExportDocument Document(params (ChatRoles Role, string Text)[] messages)
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

        var header = TranscriptHeaderDocument.Create(userId, conversationId, "Planning notes", created);

        var stored = messages
            .Select((message, index) => TranscriptMessageDocument.Create(
                userId, conversationId, Guid.NewGuid(), message.Role, message.Text, created.AddSeconds(index)))
            .ToList();

        return new ConversationExportDocument(header, stored, ExportedAt);
    }

    [Fact]
    public void Render_Conversation_ProducesAPdfWithAtLeastOnePage()
    {
        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        var export = _renderer.Render(Document(
            (ChatRoles.User, "What does retrieval do?"),
            (ChatRoles.Assistant, "It runs a tool call.")));

        Assert.Equal("application/pdf", export.ContentType);
        Assert.Equal("Planning-notes.pdf", export.FileName);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(export.Content, 0, 5));

        // Reopened rather than substring-matched: PDFsharp compresses streams, so a substring assertion
        // would test the writer's spacing rather than whether the file reads back.
        using var stream = new MemoryStream(export.Content);
        using var reopened = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.True(reopened.PageCount >= 1);
        Assert.Equal("Planning notes", reopened.Info.Title);
    }

    /// <summary>
    /// Every block kind through one document, because the renderer walks them recursively and a
    /// missing case throws rather than degrading.
    /// </summary>
    [Fact]
    public void Render_EveryBlockKind_DoesNotThrow()
    {
        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        const string Markdown = """
            # Heading one

            Prose with **bold**, _italic_, `code` and a [link](https://example.test/a).

            ## Heading two

            - bullet one
              - nested bullet
            - bullet two

            1. first
            2. second

            > quoted text
            > over two lines

            ```csharp
            var x = 1;

            var y = 2;
            ```

            | column | other |
            | --- | --- |
            | a | b |

            ---

            Trailing paragraph.
            """;

        var export = _renderer.Render(Document((ChatRoles.Assistant, Markdown)));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(export.Content, 0, 5));
    }

    [Fact]
    public void Render_EmptyConversation_StillProducesAPdf()
    {
        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        var export = _renderer.Render(Document());

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(export.Content, 0, 5));
    }

    /// <summary>
    /// The name reaches a file name and the document's own metadata, and it is user-supplied.
    /// </summary>
    [Fact]
    public void Render_ConversationNameWithPathCharacters_IsReducedToASafeFileName()
    {
        Assert.SkipUnless(_fonts.IsUsable, PdfFontFixture.SkipReason);

        var document = Document() with
        {
            Header = TranscriptHeaderDocument.Create(
                Guid.NewGuid(), Guid.NewGuid(), "../../etc/passwd", DateTimeOffset.UtcNow)
        };

        var export = _renderer.Render(document);

        Assert.Equal("etc-passwd.pdf", export.FileName);
    }
}
