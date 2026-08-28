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
/// named; anything less certain runs. See <c>docs/file-agent/the-agent.md</c> §5.2.
/// </remarks>
public static class FileAgentPreflight
{
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

        if (resolution.Unresolved.Count > 0)
        {
            var missing = string.Join("', '", resolution.Unresolved);
            var available = resolution.Available.Count == 0
                ? "This conversation holds no files at all."
                : $"What it does have: {string.Join(", ", resolution.Available)}.";

            return new FileAgentRefusal(
                FileAgentOutcomes.RefusedUnknownSource,
                $"I did not run anything. There is no file called '{missing}' in this conversation. {available}",
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
}
