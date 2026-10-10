namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a job finished its work but lost the race for the terminal state to a cancel that
/// arrived first.
/// </summary>
/// <remarks>
/// By the time it is thrown the job has already undone what it persisted, so a handler must treat it
/// as an ordinary end rather than a failure.
/// </remarks>
public class JobCancelledException : Exception
{
    public JobCancelledException(string message) : base(message) { }

    public JobCancelledException(string message, Exception innerException)
        : base(message, innerException) { }
}
