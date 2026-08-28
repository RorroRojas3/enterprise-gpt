namespace Enterprise.Gpt.Service.Agents;

/// <summary>
/// How one <c>file_agent</c> invocation ended.
/// </summary>
/// <remarks>
/// Strings rather than an enum because every one of them is a metric dimension. The refusals must stay
/// distinguishable from the failures in a query — see <see cref="IsRefusal"/>.
/// </remarks>
public static class FileAgentOutcomes
{
    /// <summary>At least one artifact was verified and stored.</summary>
    public const string Created = "created";

    /// <summary>One leg of a run finished without error, for the instruments that measure a leg.</summary>
    public const string Succeeded = "success";

    /// <summary>The requested conversion is one the confirmed matrix refuses. No sandbox run started.</summary>
    public const string RefusedConversion = "refused-conversion";

    /// <summary>A named file matched more than one document in scope. No sandbox run started.</summary>
    public const string RefusedAmbiguous = "refused-ambiguous";

    /// <summary>A named file matched nothing in scope. No sandbox run started.</summary>
    public const string RefusedUnknownSource = "refused-unknown-source";

    /// <summary>The run reached the sandbox, answered with prose, and produced no file.</summary>
    public const string NoFileProduced = "no-file-produced";

    /// <summary>Every artifact the run produced failed to re-open, and the retry bound is exhausted.</summary>
    public const string VerificationFailed = "verification-failed";

    /// <summary>The run exceeded its own wall-clock bound.</summary>
    public const string TimedOut = "timed-out";

    /// <summary>The enclosing turn was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The run threw for any other reason.</summary>
    public const string Error = "error";

    /// <summary>Whether an outcome is a refusal the agent made rather than a failure it suffered.</summary>
    /// <param name="outcome">One of the constants on this type.</param>
    /// <returns><see langword="true"/> for a refusal, which completes the activity rather than failing it.</returns>
    public static bool IsRefusal(string outcome) =>
        outcome is RefusedConversion or RefusedAmbiguous or RefusedUnknownSource;
}
