using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Builds minimal in-memory <c>.pptx</c> packages for tests.
/// </summary>
/// <remarks>
/// Generated rather than checked in, so the expected text stays visible in the test and diffable.
/// </remarks>
public static class PresentationBuilder
{
    public static byte[] Create(IReadOnlyList<string[]> slides, IReadOnlyDictionary<int, string>? notesBySlideIndex = null)
    {
        using var stream = new MemoryStream();

        using (var document = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new Presentation();

            var slideIdList = new SlideIdList();
            var slideId = 256U;

            for (var i = 0; i < slides.Count; i++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Slide(new CommonSlideData(BuildShapeTree(slides[i], placeholder: null)));

                if (notesBySlideIndex?.TryGetValue(i, out var notes) == true)
                {
                    var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                    var notesTree = BuildShapeTree(["7"], PlaceholderValues.SlideNumber);
                    notesTree.Append(BuildShape([notes], PlaceholderValues.Body));
                    notesPart.NotesSlide = new NotesSlide(new CommonSlideData(notesTree));
                }

                slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation.Append(slideIdList);
            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Opens cleanly and faults only when the dangling relationship is dereferenced, so it tests exception
    /// handling around the whole parse rather than around <c>Open()</c>.
    /// </summary>
    public static byte[] CreateWithDanglingSlideRelationship()
    {
        using var stream = new MemoryStream();

        using (var document = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new Presentation(
                new SlideIdList(new SlideId { Id = 256U, RelationshipId = "rIdDoesNotExist" }));
            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a package whose slide part contains malformed XML, which the SDK only rejects when it
    /// lazily parses that part.
    /// </summary>
    public static byte[] CreateWithCorruptSlideXml()
    {
        var content = Create([["Placeholder"]]);

        using var stream = new MemoryStream();
        stream.Write(content);
        stream.Position = 0;

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var slideEntry = archive.Entries.First(e => e.FullName.Contains("slides/slide", StringComparison.Ordinal));

            using var entryStream = slideEntry.Open();
            entryStream.SetLength(0);

            var malformed = "<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld>"u8;
            entryStream.Write(malformed);
        }

        return stream.ToArray();
    }

    private static ShapeTree BuildShapeTree(string[] paragraphs, PlaceholderValues? placeholder)
    {
        var tree = new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties());

        if (paragraphs.Length > 0)
        {
            tree.Append(BuildShape(paragraphs, placeholder));
        }

        return tree;
    }

    private static Shape BuildShape(string[] paragraphs, PlaceholderValues? placeholder)
    {
        var applicationProperties = new ApplicationNonVisualDrawingProperties();
        if (placeholder is { } placeholderValue)
        {
            applicationProperties.Append(new PlaceholderShape { Type = placeholderValue });
        }

        var textBody = new TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var paragraph in paragraphs)
        {
            textBody.Append(new A.Paragraph(new A.Run(new A.Text(paragraph))));
        }

        return new Shape(
            new NonVisualShapeProperties(
                new NonVisualDrawingProperties { Id = 2U, Name = "Content" },
                new NonVisualShapeDrawingProperties(),
                applicationProperties),
            new ShapeProperties(),
            textBody);
    }
}
