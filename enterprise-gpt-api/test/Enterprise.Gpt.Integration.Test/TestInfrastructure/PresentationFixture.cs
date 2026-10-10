using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Builds a minimal in-memory <c>.pptx</c> package so upload tests can exercise the PowerPoint path
/// without a committed binary fixture.
/// </summary>
public static class PresentationFixture
{
    /// <summary>
    /// Creates a presentation with the given number of slides, each carrying enough prose to produce
    /// several chunks.
    /// </summary>
    /// <param name="slideCount">How many slides to generate.</param>
    /// <returns>The bytes of the generated package.</returns>
    public static byte[] Create(int slideCount)
    {
        using var stream = new MemoryStream();

        using (var document = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new Presentation();

            var slideIdList = new SlideIdList();
            var slideId = 256U;

            for (var i = 1; i <= slideCount; i++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new Slide(new CommonSlideData(BuildShapeTree(i)));

                slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation.Append(slideIdList);
            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    private static ShapeTree BuildShapeTree(int slideNumber)
    {
        var textBody = new TextBody(new A.BodyProperties(), new A.ListStyle());
        textBody.Append(new A.Paragraph(new A.Run(new A.Text($"Slide {slideNumber} title"))));

        for (var i = 1; i <= 12; i++)
        {
            textBody.Append(new A.Paragraph(new A.Run(new A.Text(
                $"Bullet {i} on slide {slideNumber} describes an entirely unremarkable and deliberately verbose subject."))));
        }

        return new ShapeTree(
            new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(),
            new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = 2U, Name = "Content" },
                    new NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new ShapeProperties(),
                textBody));
    }
}
