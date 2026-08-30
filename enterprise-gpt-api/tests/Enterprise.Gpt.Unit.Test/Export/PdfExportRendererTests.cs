using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Export.Fonts;
using Enterprise.Gpt.Service.Export.Renderers;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Export;

/// <summary>
/// Exercises the PDF renderer end to end, against whatever fonts the machine running the suite has.
/// </summary>
/// <remarks>
/// <para>
/// <c>GlobalFontSettings.FontResolver</c> is process-global and documented as set-once, so every test
/// here shares one resolver installed by the fixture rather than each installing its own — a second
/// assignment after a font operation is what PDFsharp warns about, and xUnit runs classes in
/// parallel.
/// </para>
/// <para>
/// The assertions are structural: that the bytes are a PDF, that they carry pages, and that the
/// renderer survives every block kind. What the glyphs look like is not asserted, because it depends
/// on which faces the host resolved — the layout decisions themselves are covered by
/// <see cref="MarkdownBlockMapperTests"/>, which is host-independent.
/// </para>
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

        // Reopened rather than pattern-matched against the bytes: PDFsharp writes its own object
        // syntax and compresses streams, so a substring assertion tests the writer's spacing rather
        // than whether anything can read the file back.
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

/// <summary>
/// Installs one process-wide font resolver for every PDF test.
/// </summary>
public sealed class PdfFontFixture
{
    /// <summary>
    /// Why the PDF tests skip when they do — a machine with no font PDFsharp can read is exactly the
    /// deployment the <c>export-renderer-not-configured</c> problem exists for, so it is a skip
    /// rather than a failure.
    /// </summary>
    public const string SkipReason =
        "No usable export font was found on this machine, so PDF rendering is unavailable here — "
        + "the same condition that makes the route answer 503. See Export/Fonts/README.md.";

    public PdfFontFixture()
    {
        var resolver = new ExportFontResolver();
        IsUsable = resolver.IsUsable;

        if (IsUsable)
        {
            GlobalFontSettings.FontResolver = resolver;
        }
    }

    /// <summary>Gets whether this machine resolved a body face.</summary>
    public bool IsUsable { get; }
}

/// <summary>
/// Serializes the PDF tests, because the font resolver they share is process-global.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PdfFontCollection : ICollectionFixture<PdfFontFixture>
{
    public const string Name = "PdfFonts";
}
