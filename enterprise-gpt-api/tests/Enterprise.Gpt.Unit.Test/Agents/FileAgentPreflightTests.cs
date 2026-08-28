using Enterprise.Gpt.Service.Agents;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers what a run refuses before it opens a sandbox session — and, just as importantly, what it
/// declines to refuse.
/// </summary>
/// <remarks>
/// The pair enumerated below is the whole published matrix. A structural conversion quietly refused
/// costs a user a file they could have had, and nothing else in the suite would notice.
/// </remarks>
public sealed class FileAgentPreflightTests
{
    private static readonly ConversionMatrix _matrix = ConversionMatrix.Load();

    public static TheoryData<string, string, string> EveryCell()
    {
        TheoryData<string, string, string> cells = [];

        foreach (var cell in _matrix.Cells)
        {
            cells.Add(cell.From, cell.To, cell.Tier.ToString());
        }

        return cells;
    }

    [Theory]
    [MemberData(nameof(EveryCell))]
    public void Evaluate_EveryPublishedCell_IsRefusedOnlyWhenTheMatrixRefusesIt(string from, string to, string tier)
    {
        var source = $"source.{from}";
        var resolution = Resolution(Matched(source), available: [source]);

        var refusal = FileAgentPreflight.Evaluate(resolution, $"Convert {source} to {to}", _matrix);

        if (tier == nameof(ConversionTiers.Refused))
        {
            Assert.NotNull(refusal);
            Assert.Equal(FileAgentOutcomes.RefusedConversion, refusal.Outcome);
            Assert.Contains(from, refusal.Text, StringComparison.Ordinal);
            Assert.Contains(to, refusal.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(refusal);
        }
    }

    [Fact]
    public void Evaluate_ARefusedPair_SuggestsWhatTheSourceCanReach()
    {
        var resolution = Resolution(Matched("deck.pptx"), available: ["deck.pptx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "Convert deck.pptx to pdf", _matrix);

        Assert.NotNull(refusal);
        Assert.Contains("md", refusal.Text, StringComparison.Ordinal);
        Assert.Contains("txt", refusal.Text, StringComparison.Ordinal);
    }

    // The instruction names its own source, whose extension reads as a format too. Without removing it
    // the scan finds two targets, and a run that can never name exactly one can never be refused.
    [Fact]
    public void Evaluate_ARefusedPairWhoseSourceNamesItsOwnFormat_StillRefuses()
    {
        var resolution = Resolution(Matched("quarterly deck.pptx"), available: ["quarterly deck.pptx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "turn quarterly deck.pptx into a pdf", _matrix);

        Assert.NotNull(refusal);
        Assert.Equal(FileAgentOutcomes.RefusedConversion, refusal.Outcome);
    }

    [Fact]
    public void Evaluate_MoreThanOneTargetNamed_Runs()
    {
        var resolution = Resolution(Matched("deck.pptx"), available: ["deck.pptx"]);

        Assert.Null(FileAgentPreflight.Evaluate(
            resolution, "Convert deck.pptx to pdf or markdown, whichever works", _matrix));
    }

    [Fact]
    public void Evaluate_MoreThanOneSourceMatched_Runs()
    {
        var resolution = Resolution(
            [.. Matched("deck.pptx"), .. Matched("notes.docx")], available: ["deck.pptx", "notes.docx"]);

        Assert.Null(FileAgentPreflight.Evaluate(resolution, "Compare deck.pptx and notes.docx as pdf", _matrix));
    }

    [Fact]
    public void Evaluate_NoSourceAtAll_Runs()
    {
        Assert.Null(FileAgentPreflight.Evaluate(Resolution([]), "Make me a pdf about quarterly revenue", _matrix));
    }

    [Fact]
    public void Evaluate_ANameMatchingMoreThanOneDocument_AsksWhichRatherThanGuessing()
    {
        var resolution = Resolution([], ambiguous: ["report.docx"], available: ["report.docx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "Convert report.docx to pdf", _matrix);

        Assert.NotNull(refusal);
        Assert.Equal(FileAgentOutcomes.RefusedAmbiguous, refusal.Outcome);
        Assert.Contains("report.docx", refusal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ANameMatchingNothing_SaysWhatIsAvailable()
    {
        var resolution = Resolution([], unresolved: ["missing.docx"], available: ["present.docx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "Convert missing.docx to pdf", _matrix);

        Assert.NotNull(refusal);
        Assert.Equal(FileAgentOutcomes.RefusedUnknownSource, refusal.Outcome);
        Assert.Contains("missing.docx", refusal.Text, StringComparison.Ordinal);
        Assert.Contains("present.docx", refusal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ANameMatchingNothingInAnEmptyConversation_SaysThereAreNoFiles()
    {
        var resolution = Resolution([], unresolved: ["missing.docx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "Convert missing.docx to pdf", _matrix);

        Assert.NotNull(refusal);
        Assert.Contains("no files at all", refusal.Text, StringComparison.Ordinal);
    }

    // Every refusal has to be tellable apart from every other one on the card, or the four failure
    // states the timeline promises collapse into one.
    [Fact]
    public void Evaluate_TheThreeRefusals_CarryThreeDifferentSubStatuses()
    {
        var conversion = FileAgentPreflight.Evaluate(
            Resolution(Matched("deck.pptx"), available: ["deck.pptx"]), "Convert deck.pptx to pdf", _matrix);
        var ambiguous = FileAgentPreflight.Evaluate(
            Resolution([], ambiguous: ["report.docx"]), "Convert report.docx to pdf", _matrix);
        var unknown = FileAgentPreflight.Evaluate(
            Resolution([], unresolved: ["missing.docx"]), "Convert missing.docx to pdf", _matrix);

        string[] subStatuses = [conversion!.SubStatus, ambiguous!.SubStatus, unknown!.SubStatus];

        Assert.Equal(3, subStatuses.Distinct(StringComparer.Ordinal).Count());
    }

    private static List<FileAgentSourceMatch> Matched(string name) =>
        [new(Guid.NewGuid(), name, Path.GetExtension(name))];

    private static FileAgentSourceResolution Resolution(
        List<FileAgentSourceMatch> matched,
        string[]? ambiguous = null,
        string[]? unresolved = null,
        string[]? available = null) =>
        new(matched, ambiguous ?? [], unresolved ?? [], available ?? []);
}
