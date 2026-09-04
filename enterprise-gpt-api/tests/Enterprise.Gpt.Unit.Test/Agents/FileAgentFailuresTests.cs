using Enterprise.Gpt.Service.Agents;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Agents;

/// <summary>
/// Covers what a run that produced no file tells the user and the model.
/// </summary>
/// <remarks>
/// The distinctness assertion is the point. A reader of the timeline has to be able to tell an artifact
/// that would not open from a run that never made one, and the copy is the only thing that says which —
/// progress events deliberately carry no error text.
/// </remarks>
public sealed class FileAgentFailuresTests
{
    private static readonly ConversionMatrix _matrix = ConversionMatrix.Load();

    private static readonly FileAgentFailure[] _failures =
    [
        FileAgentFailures.NoFileProduced(),
        FileAgentFailures.VerificationFailed("'notes.docx' did not open when checked: it is empty."),
        FileAgentFailures.TimedOut(300),
        FileAgentFailures.Unexpected()
    ];

    public static TheoryData<FileAgentFailure> EveryFailure() => [.. _failures];

    [Fact]
    public void Failures_TheFourWaysARunCanFail_EachReadDifferently()
    {
        Assert.Equal(4, _failures.Select(failure => failure.SubStatus).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, _failures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, _failures.Select(failure => failure.Outcome).Distinct(StringComparer.Ordinal).Count());
    }

    // The card shows one line per outcome and the assistant relays the other. Both have to say something,
    // and neither may be the same generic sentence.
    [Theory]
    [MemberData(nameof(EveryFailure))]
    public void Failure_EachOne_CarriesALineForTheCardAndASentenceForTheAssistant(FileAgentFailure failure)
    {
        Assert.False(string.IsNullOrWhiteSpace(failure.SubStatus));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.False(FileAgentOutcomes.IsRefusal(failure.Outcome));
    }

    // Seven outcomes reach the card, and a reader must be able to tell every one of them apart — the
    // three refusals included, which complete the activity where these four fail it.
    [Fact]
    public void Failures_AndTheRefusalsBesideThem_AreSevenDistinctLines()
    {
        List<string> lines =
        [
            .. _failures.Select(failure => failure.SubStatus),
            Refusal("Convert report.pdf to pptx", Matched("report.pdf")).SubStatus,
            Refusal("Convert report.docx to pdf", [], ambiguous: ["report.docx"]).SubStatus,
            Refusal(
                "Convert missing.docx to pdf",
                [],
                unresolved: ["missing.docx"],
                available: ["present.docx"]).SubStatus
        ];

        Assert.Equal(7, lines.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TimedOut_TheConfiguredBound_NamesItSoTheRightKnobGetsTurned()
    {
        Assert.Contains("300", FileAgentFailures.TimedOut(300).Message, StringComparison.Ordinal);
    }

    // It reaches the model's context, where a storage or database failure would name an account, a
    // container and a path.
    [Fact]
    public void Unexpected_TheMessage_CarriesNothingOfTheExceptionThatCausedIt()
    {
        var failure = FileAgentFailures.Unexpected();

        Assert.DoesNotContain("Exception", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FileAgentRefusal Refusal(
        string instruction,
        List<FileAgentSourceMatch> matched,
        string[]? ambiguous = null,
        string[]? unresolved = null,
        string[]? available = null)
    {
        var resolution = new FileAgentSourceResolution(
            matched, ambiguous ?? [], unresolved ?? [], available ?? []);

        return FileAgentPreflight.Evaluate(resolution, instruction, _matrix)
            ?? throw new InvalidOperationException($"'{instruction}' was expected to be refused.");
    }

    private static List<FileAgentSourceMatch> Matched(string name) =>
        [new(Guid.NewGuid(), name, Path.GetExtension(name))];
}
