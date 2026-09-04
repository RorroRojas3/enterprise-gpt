using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>A request the agent answers without opening a sandbox session at all.</summary>
/// <param name="Outcome">One of <see cref="FileAgentOutcomes"/>' three refusals.</param>
/// <param name="Text">The agent's own answer, which the assistant relays.</param>
/// <param name="SubStatus">
/// The line the activity card shows. Distinct per outcome, because a reader has to be able to tell an
/// unsupported conversion from a name that matched nothing.
/// </param>
public sealed record FileAgentRefusal(string Outcome, string Text, string SubStatus);

/// <summary>
/// Decides, before any sandbox session is opened, what a run cannot serve.
/// </summary>
/// <remarks>
/// Refuses only a cell the confirmed matrix records as refused, with exactly one source and one target
/// named; anything less certain runs. See <c>docs/tools/file-agent.md</c>.
/// </remarks>
public static partial class FileAgentPreflight
{
    /// <summary>
    /// The words that make a name the run's own output rather than something it has to find.
    /// </summary>
    /// <remarks>
    /// One-sided on purpose: a create misread as a read costs the user the feature outright, while a
    /// read misread as a create costs a sandbox session and, worse, can hand back a plausible document
    /// built from nothing — which is why <see cref="ReadsFromAMissingName"/> overrides this.
    /// "Save" and "export" are absent: both read as "export this file to that format" far more often
    /// than as a create.
    /// </remarks>
    private static readonly FrozenSet<string> _writingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create", "creating", "generate", "generating", "make", "making", "produce", "producing",
        "write", "writing", "draft", "drafting", "build", "building", "compose", "composing",
        "prepare", "preparing", "assemble", "assembling", "output", "call", "called", "name", "named"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Weighs one run's resolved sources and its instruction against the confirmed matrix.
    /// </summary>
    /// <param name="resolution">What the instruction named, from <see cref="IFileAgentDocumentReader"/>.</param>
    /// <param name="instruction">The run's instruction.</param>
    /// <param name="matrix">The confirmed conversion matrix.</param>
    /// <returns>The refusal, or <see langword="null"/> when the run should proceed.</returns>
    public static FileAgentRefusal? Evaluate(
        FileAgentSourceResolution resolution, string instruction, IConversionMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(matrix);

        if (resolution.Ambiguous.Count > 0)
        {
            var names = string.Join("', '", resolution.Ambiguous);

            return new FileAgentRefusal(
                FileAgentOutcomes.RefusedAmbiguous,
                $"I did not run anything. More than one file in this conversation is called '{names}', so I cannot "
                    + "tell which was meant. Ask which one, then ask me again.",
                "More than one file has that name");
        }

        if (NamesAMissingSource(resolution, instruction))
        {
            var missing = string.Join("', '", resolution.Unresolved);

            return new FileAgentRefusal(
                FileAgentOutcomes.RefusedUnknownSource,
                $"I did not run anything. There is no file called '{missing}' in this conversation. "
                    + $"What it does have: {string.Join(", ", resolution.Available)}.",
                "No file here by that name");
        }

        if (resolution.Matched.Count != 1)
        {
            return null;
        }

        var source = resolution.Matched[0];
        var targets = FileAgentFormats.DetectTargets(instruction, [source.Name]);

        if (targets.Count != 1
            || matrix.Find(source.Extension, targets[0]) is not { Tier: ConversionTiers.Refused } cell)
        {
            return null;
        }

        var alternatives = matrix.OfferedTargets(source.Extension);
        var suggestion = alternatives.Count == 0
            ? string.Empty
            : $" You could ask for {string.Join(", ", alternatives)} instead.";

        return new FileAgentRefusal(
            FileAgentOutcomes.RefusedConversion,
            $"I did not run anything. Converting {cell.From} to {cell.To} is not supported here. {cell.Note}{suggestion}",
            $"{cell.From} to {cell.To} is not supported");
    }

    /// <summary>Whether an unmatched name is a source the run needs rather than a file it would write.</summary>
    /// <remarks>
    /// Nothing in an instruction says which direction a name points, so the refusal is held to the case
    /// where it can only be a source: something was there to be named, nothing resolved, and either
    /// nothing asks for a file to be written or one of the names is read from regardless.
    /// </remarks>
    private static bool NamesAMissingSource(FileAgentSourceResolution resolution, string instruction) =>
        resolution.Unresolved.Count > 0
        && resolution.Available.Count > 0
        && resolution.Matched.Count == 0
        && (ReadsFromAMissingName(instruction, resolution.Unresolved)

            // Struck out first: a name is a bag of ordinary words once split, and "draft.docx" would
            // otherwise supply the very verb that proves the request is a create.
            || !FileAgentFormats
                .Words(FileAgentFormats.Without(instruction, resolution.Unresolved))
                .Overlaps(_writingWords));

    /// <summary>Whether the instruction reads one of these names out of the conversation.</summary>
    /// <remarks>
    /// A create says what to put in a file; only a read says where the content comes from. The
    /// preposition settles a name the surrounding verb would otherwise claim, as in "write a summary of
    /// q3-report.pdf", which is a read however it opens.
    /// </remarks>
    private static bool ReadsFromAMissingName(string instruction, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var index = instruction.IndexOf(name, StringComparison.OrdinalIgnoreCase);

            if (index > 0 && SourcePreposition().IsMatch(instruction[..index]))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\b(?:from|of|in|within|using|between|against)\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePreposition();
}
