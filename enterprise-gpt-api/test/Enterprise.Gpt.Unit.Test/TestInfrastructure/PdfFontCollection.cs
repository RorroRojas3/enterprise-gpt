using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Exports.Fonts;
using Enterprise.Gpt.Service.Exports.Models;
using Enterprise.Gpt.Service.Exports.Renderers;
using Enterprise.Gpt.Service.Exports;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
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
