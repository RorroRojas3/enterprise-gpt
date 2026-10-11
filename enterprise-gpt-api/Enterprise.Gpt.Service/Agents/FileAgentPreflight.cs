using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Agents.Models;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// Decides, before any sandbox session is opened, what a run cannot serve.
/// </summary>
/// <remarks>
/// Refuses only a cell the confirmed matrix records as refused, with exactly one source and one target
/// named; anything less certain runs. See <c>docs/tools/file-agent.md</c>.
/// </remarks>
public static partial class FileAgentPreflight
{
    // Words that make a name the run's output. A read misread as a create can fabricate a document from
    // nothing, so ReadsFromAMissingName overrides this; "save" and "export" usually mean converting a file.
    private static readonly FrozenSet<string> _writingWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create", "creating", "generate", "generating", "make", "making", "produce", "producing",
        "write", "writing", "draft", "drafting", "build", "building", "compose", "composing",
        "prepare", "preparing", "assemble", "assembling", "output", "call", "called", "name", "named"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    #region Private methods

    /// <summary>Whether an unmatched name is a source the run needs rather than a file it would write.</summary>
    /// <remarks>
    /// An instruction does not say which way a name points, so this holds to the case where it can only be
    /// a source: nothing resolved, and either nothing asks for a write or a name is read from.
    /// </remarks>
    private static bool NamesAMissingSource(FileAgentSourceResolution resolution, string instruction) =>
        resolution.Unresolved.Count > 0
        && resolution.Available.Count > 0
        && resolution.Matched.Count == 0
        && (ReadsFromAMissingName(instruction, resolution.Unresolved)

            // Names struck out first, or "draft.docx" would supply the verb that marks a create.
            || !FileAgentFormats
                .Words(FileAgentFormats.Without(instruction, resolution.Unresolved))
                .Overlaps(_writingWords));

    /// <summary>Whether the instruction reads one of these names out of the conversation.</summary>
    /// <remarks>The preposition settles direction: "write a summary of q3-report.pdf" is a read.</remarks>
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

    #endregion

    #region Public static methods

    /// <summary>Weighs one run's resolved sources and its instruction against the confirmed matrix.</summary>
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

    #endregion
}
