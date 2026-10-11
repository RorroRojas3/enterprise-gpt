using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Agents;
using Enterprise.Gpt.Service.Agents.Models;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Agents;

/// <remarks>
/// The pairs enumerated below are the whole published matrix: a structural conversion quietly refused
/// costs a user a file, and nothing else in the suite would notice.
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
        var resolution = Resolution(Matched("report.pdf"), available: ["report.pdf"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "Convert report.pdf to pptx", _matrix);

        Assert.NotNull(refusal);
        Assert.Contains("docx", refusal.Text, StringComparison.Ordinal);
        Assert.Contains("md", refusal.Text, StringComparison.Ordinal);
    }

    // The instruction names its own source, whose extension reads as a format too. Without removing it
    // the scan finds two targets, and a run that can never name exactly one can never be refused.
    [Fact]
    public void Evaluate_ARefusedPairWhoseSourceNamesItsOwnFormat_StillRefuses()
    {
        var resolution = Resolution(Matched("quarterly report.pdf"), available: ["quarterly report.pdf"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, "turn quarterly report.pdf into a pptx", _matrix);

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

    // With no files at all there is no source to be missing, so the only reading left is a name for
    // what the run is about to write.
    [Fact]
    public void Evaluate_ANameMatchingNothingWhenTheConversationHoldsNoFiles_Runs()
    {
        var resolution = Resolution([], unresolved: ["random.docx"]);

        Assert.Null(FileAgentPreflight.Evaluate(resolution, "Create random.docx with sample content", _matrix));
    }

    // A create naming its own answer: the format appears in words as often as by extension, so neither
    // may stand in for "this file must exist". The name is per row because it is part of what is read.
    [Theory]
    [InlineData("Create summary.docx about last quarter", "summary.docx")]
    [InlineData("Create a Word document called notes.docx", "notes.docx")]
    [InlineData("Generate a spreadsheet named budget.xlsx", "budget.xlsx")]
    [InlineData("Write a deck called kickoff.pptx", "kickoff.pptx")]
    [InlineData("Produce a pdf and save it as summary.pdf", "summary.pdf")]
    [InlineData("**Create** the draft.docx we discussed", "draft.docx")]
    public void Evaluate_ACreateNamingTheFileItWillWrite_Runs(string instruction, string name)
    {
        var resolution = Resolution([], unresolved: [name], available: ["present.docx"]);

        Assert.Null(FileAgentPreflight.Evaluate(resolution, instruction, _matrix));
    }

    [Fact]
    public void Evaluate_AnOutputNameBesideAResolvedSource_Runs()
    {
        var resolution = Resolution(
            Matched("report.docx"), unresolved: ["report-v2.docx"], available: ["report.docx"]);

        Assert.Null(FileAgentPreflight.Evaluate(
            resolution, "Edit report.docx and save it as report-v2.docx", _matrix));
    }

    // The other side: "draft" and "output" are ordinary file names, and writing something *from* a file
    // is a read whatever verb opens it.
    [Theory]
    [InlineData("Summarize missing.docx", "missing.docx")]
    [InlineData("Fix the totals in missing.docx", "missing.docx")]
    [InlineData("Convert draft.docx to pdf", "draft.docx")]
    [InlineData("Convert output.xlsx to csv", "output.xlsx")]
    [InlineData("Export budget.xlsx to csv", "budget.xlsx")]
    [InlineData("Write a one-page summary of q3-report.pdf", "q3-report.pdf")]
    [InlineData("Create a deck from sales.xlsx", "sales.xlsx")]
    public void Evaluate_AReadNamingAFileThatIsNotHere_Refuses(string instruction, string name)
    {
        var resolution = Resolution([], unresolved: [name], available: ["present.docx"]);

        var refusal = FileAgentPreflight.Evaluate(resolution, instruction, _matrix);

        Assert.NotNull(refusal);
        Assert.Equal(FileAgentOutcomes.RefusedUnknownSource, refusal.Outcome);
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
