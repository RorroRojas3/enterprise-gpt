using System.Diagnostics;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Service.Observability;

/// <summary>The span one File Agent run produces.</summary>
/// <remarks>
/// On <see cref="TelemetryNames.ChatSource"/> so it is exported and nests under the turn's trace. No tag
/// carries prompt content, tool arguments, generated source, file names or signed URLs.
/// </remarks>
public static class FileAgentTracing
{
    private static readonly ActivitySource Source = new(TelemetryNames.ChatSource);

    #region Public static methods

    /// <summary>Starts the span covering one <c>file_agent</c> invocation.</summary>
    public static Activity? StartRun() => Source.StartActivity("file_agent.run", ActivityKind.Internal);

    /// <summary>Records how a run ended on its span.</summary>
    /// <param name="activity">The span, or <see langword="null"/> when the run was not sampled.</param>
    /// <param name="outcome">One of <see cref="Agents.FileAgentOutcomes"/>.</param>
    public static void Complete(Activity? activity, string outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("file_agent.outcome", outcome);

        // A refusal is a correct answer: only outcomes that leave the user without the file are errors.
        activity.SetStatus(
            outcome is Agents.FileAgentOutcomes.Created || Agents.FileAgentOutcomes.IsRefusal(outcome)
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error);
    }

    #endregion
}
