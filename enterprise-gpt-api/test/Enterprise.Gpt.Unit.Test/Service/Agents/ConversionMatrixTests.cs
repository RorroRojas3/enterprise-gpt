using Enterprise.Gpt.Service.Agents;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers the loader the run's pre-flight reads the published matrix through, against the real file
/// that shipped with the build.
/// </summary>
/// <remarks>
/// The file's own internal consistency is <see cref="ConversionMatrixDocumentTests"/>' subject. This
/// one is about what the code does with it, which is a separate way to be wrong.
/// </remarks>
public sealed class ConversionMatrixTests
{
    private static readonly ConversionMatrix _matrix = ConversionMatrix.Load();

    [Fact]
    public void Load_TheDeployedMatrix_CoversEveryOrderedPairOfFormats()
    {
        var expected = _matrix.Formats.Count * (_matrix.Formats.Count - 1);

        Assert.Equal(expected, _matrix.Cells.Count);
        Assert.Equal(FileAgentFormats.Producible.Order(), _matrix.Formats.Order());
    }

    [Theory]
    [InlineData("docx", "md")]
    [InlineData(".docx", ".md")]
    [InlineData("DOCX", "MD")]
    public void Find_AFormatWrittenAnyWay_ResolvesToTheSameCell(string from, string to)
    {
        var cell = _matrix.Find(from, to);

        Assert.NotNull(cell);
        Assert.Equal("docx", cell.From);
        Assert.Equal("md", cell.To);
    }

    [Fact]
    public void Find_AFormatTheMatrixDoesNotCover_ReturnsNothing()
    {
        Assert.Null(_matrix.Find("xlsm", "pdf"));
        Assert.Null(_matrix.Find(null, null));
    }

    [Fact]
    public void OfferedTargets_ASource_ListsOnlyWhatItCanActuallyReach()
    {
        var targets = _matrix.OfferedTargets("pdf");

        Assert.Equal(["docx", "xlsx", "csv", "md", "txt"], targets);
        Assert.DoesNotContain("pptx", targets);
    }

    [Fact]
    public void Load_AMatrixThatIsNotThere_SaysWhichFileIsMissing()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "no-such-conversion-matrix.json");

        var exception = Assert.Throws<FileNotFoundException>(() => ConversionMatrix.Load(missing));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }
}
