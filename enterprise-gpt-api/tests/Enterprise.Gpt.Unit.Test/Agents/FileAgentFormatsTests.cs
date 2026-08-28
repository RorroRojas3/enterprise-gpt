using Enterprise.Gpt.Service.Agents;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers reading a target format out of an instruction, which is what decides whether a conversion is
/// certain enough to refuse.
/// </summary>
public sealed class FileAgentFormatsTests
{
    [Theory]
    [InlineData("Turn this into a pdf", "pdf")]
    [InlineData("Give me a Word document", "docx")]
    [InlineData("Make it an Excel workbook", "xlsx")]
    [InlineData("Build a slide deck", "pptx")]
    [InlineData("Export it as markdown", "md")]
    [InlineData("Save it as txt", "txt")]
    [InlineData("Just the rows, as CSV.", "csv")]
    public void DetectTargets_AFormatNamedInWords_FindsIt(string instruction, string expected)
    {
        Assert.Equal([expected], FileAgentFormats.DetectTargets(instruction, []));
    }

    // Substring matching would find "md" inside "command" and "text" inside "context", and a topic that
    // matches everything makes every request name every format.
    [Theory]
    [InlineData("Run the command and report back")]
    [InlineData("Summarize the context of the meeting")]
    [InlineData("Rewordings are fine")]
    public void DetectTargets_AFormatOnlyOccurringInsideAnotherWord_FindsNothing(string instruction)
    {
        Assert.Empty(FileAgentFormats.DetectTargets(instruction, []));
    }

    // The instruction names its own source, whose extension is a format word too. Left in, no request
    // naming a source ever names exactly one target, and the refusal path becomes unreachable.
    [Fact]
    public void DetectTargets_ASourceWhoseNameIsAlsoAFormat_ExcludesItFromTheTargets()
    {
        var targets = FileAgentFormats.DetectTargets("convert deck.pptx to pdf", ["deck.pptx"]);

        Assert.Equal(["pdf"], targets);
    }

    [Fact]
    public void DetectTargets_SeveralFormatsNamed_ReturnsThemInThePublishedOrder()
    {
        var targets = FileAgentFormats.DetectTargets("a markdown copy and a pdf", []);

        Assert.Equal(["pdf", "md"], targets);
    }

    [Fact]
    public void DetectTargets_NoInstruction_FindsNothing()
    {
        Assert.Empty(FileAgentFormats.DetectTargets(null, []));
        Assert.Empty(FileAgentFormats.DetectTargets("   ", []));
    }

    // "document" and "sheet" read as a format in one sentence and as a common noun in the next, and a
    // wrong reading here refuses a conversion rather than merely advertising an unwanted skill.
    [Theory]
    [InlineData("Summarize the document")]
    [InlineData("Read the second sheet")]
    public void DetectTargets_AWordThatIsAlsoACommonNoun_IsNotAFormat(string instruction)
    {
        Assert.Empty(FileAgentFormats.DetectTargets(instruction, []));
    }
}
