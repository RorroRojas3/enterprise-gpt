namespace Enterprise.Gpt.Service.Agents;

/// <summary>A run that ended without a file, and what each audience is told about it.</summary>
/// <param name="Outcome">One of <see cref="FileAgentOutcomes"/>' four failures.</param>
/// <param name="Message">What the assistant is handed, to relay in its own words.</param>
/// <param name="SubStatus">The line the activity card shows.</param>
public sealed record FileAgentFailure(string Outcome, string Message, string SubStatus);

/// <summary>
/// The wording of every way a run can fail.
/// </summary>
/// <remarks>
/// Here rather than at the throw sites so the four are visibly distinct and can be asserted so: a
/// reader of the timeline has to be able to tell an artifact that would not open from a run that never
/// made one, and a single "something went wrong" for all four is the defect this exists to prevent.
/// </remarks>
public static class FileAgentFailures
{
    /// <summary>The run reached the sandbox, answered, and produced nothing.</summary>
    /// <param name="detail">Anything the run wants to add, or empty.</param>
    /// <returns>The failure.</returns>
    public static FileAgentFailure NoFileProduced(string detail = "") =>
        new(
            FileAgentOutcomes.NoFileProduced,
            $"The run finished without producing a file, so nothing was saved.{Space(detail)} Tell the user no "
                + "file was produced. Do not describe a file as though the user has one.",
            "No file was produced");

    /// <summary>Every artifact failed to re-open, and the retry bound is spent.</summary>
    /// <param name="detail">What did not open, in the verifier's own words.</param>
    /// <returns>The failure.</returns>
    public static FileAgentFailure VerificationFailed(string detail) =>
        new(
            FileAgentOutcomes.VerificationFailed,
            $"The file was produced but would not open when checked, so it was not saved.{Space(detail)} Tell "
                + "the user the file could not be produced correctly, and say what did not match. Do not offer "
                + "them a file.",
            "The file did not open when checked");

    /// <summary>The run exceeded its own wall-clock bound.</summary>
    /// <param name="seconds">The bound, named so the right knob gets turned.</param>
    /// <returns>The failure.</returns>
    public static FileAgentFailure TimedOut(int seconds) =>
        new(
            FileAgentOutcomes.TimedOut,
            $"Building the file took longer than the {seconds} second limit and was stopped. Tell the user, and "
                + "suggest a smaller or simpler file.",
            "Stopped at the time limit");

    /// <summary>Anything else threw.</summary>
    /// <returns>The failure.</returns>
    /// <remarks>
    /// Carries nothing of the exception: this message reaches the model's context, and a storage or
    /// database failure names an account, a container and a path.
    /// </remarks>
    public static FileAgentFailure Unexpected() =>
        new(
            FileAgentOutcomes.Error,
            "The file could not be produced because something went wrong inside the platform. Tell the user it "
                + "failed, and do not offer them a file.",
            "The run could not be completed");

    private static string Space(string detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
}
