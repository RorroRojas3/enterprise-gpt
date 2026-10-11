using Xunit;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Serializes the PDF tests, because the font resolver they share is process-global.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PdfFontCollection : ICollectionFixture<PdfFontFixture>
{
    public const string Name = "PdfFonts";
}
