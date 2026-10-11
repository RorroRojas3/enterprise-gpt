using Enterprise.Gpt.Service.Agents.Models;

namespace Enterprise.Gpt.Service.Agents;

/// <summary>The wording of every way a run can fail.</summary>
/// <remarks>
/// Kept together so the four stay distinct and testable: the timeline must tell an artifact that would not
/// open from a run that never made one.
/// </remarks>
public static class FileAgentFailures
{
    #region Private methods

    private static string Space(string detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";

    #endregion

    #region Public static methods

    /// <summary>The run reached the sandbox, answered, and produced nothing.</summary>
    public static FileAgentFailure NoFileProduced(string detail = "") =>
        new(
            FileAgentOutcomes.NoFileProduced,
            $"The run finished without producing a file, so nothing was saved.{Space(detail)} Tell the user no "
                + "file was produced. Do not describe a file as though the user has one.",
            "No file was produced");

    /// <summary>Every artifact failed to re-open, and the retry bound is spent.</summary>
    public static FileAgentFailure VerificationFailed(string detail) =>
        new(
            FileAgentOutcomes.VerificationFailed,
            $"The file was produced but would not open when checked, so it was not saved.{Space(detail)} Tell "
                + "the user the file could not be produced correctly, and say what did not match. Do not offer "
                + "them a file.",
            "The file did not open when checked");

    /// <summary>The run exceeded its own wall-clock bound.</summary>
    public static FileAgentFailure TimedOut(int seconds) =>
        new(
            FileAgentOutcomes.TimedOut,
            $"Building the file took longer than the {seconds} second limit and was stopped. Tell the user, and "
                + "suggest a smaller or simpler file.",
            "Stopped at the time limit");

    /// <summary>Anything else threw.</summary>
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

    #endregion
}
