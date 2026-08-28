using System.Diagnostics;
using Enterprise.Gpt.Common.Observability;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>
/// The span one File Agent run produces.
/// </summary>
/// <remarks>
/// Named with <see cref="TelemetryNames.ChatSource"/> so the exporter registration that already adds
/// that source picks it up with no wiring change, and so a run sits under the turn that caused it in
/// the same trace. No tag carries prompt content, a tool argument, generated source, a file name or a
/// signed URL — the outcome and the elapsed time are what a slow or failing run is diagnosed from.
/// </remarks>
public static class FileAgentTracing
{
    private static readonly ActivitySource Source = new(TelemetryNames.ChatSource);

    /// <summary>
    /// Starts the span covering one <c>file_agent</c> invocation.
    /// </summary>
    /// <returns>
    /// The span, or <see langword="null"/> when nothing is listening — the caller null-conditionally
    /// tags it, so an unsampled run costs nothing.
    /// </returns>
    public static Activity? StartRun() => Source.StartActivity("file_agent.run", ActivityKind.Internal);

    /// <summary>
    /// Records how a run ended on its span.
    /// </summary>
    /// <param name="activity">The span, or <see langword="null"/> when the run was not sampled.</param>
    /// <param name="outcome">One of <see cref="Agents.FileAgentOutcomes"/>.</param>
    public static void Complete(Activity? activity, string outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("file_agent.outcome", outcome);

        // A refusal is a correct answer, not an error: only the outcomes that leave the user without
        // the file they asked for set the error status.
        activity.SetStatus(
            outcome is Agents.FileAgentOutcomes.Created || Agents.FileAgentOutcomes.IsRefusal(outcome)
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error);
    }
}
