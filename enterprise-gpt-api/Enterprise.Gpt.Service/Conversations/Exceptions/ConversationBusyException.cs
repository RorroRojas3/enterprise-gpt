using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Conversations.Exceptions;

/// <summary>
/// Thrown when a turn is requested for a conversation that already has one in flight.
/// </summary>
/// <remarks>
/// A conflict rather than a bad request, so a caller can tell transient contention from a malformed
/// request and retry once the current turn completes.
/// </remarks>
public class ConversationBusyException : ConflictException
{
    /// <summary>Initializes a new instance of the <see cref="ConversationBusyException"/> class.</summary>
    public ConversationBusyException(string message)
        : base(ProblemCodes.ConversationBusy, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConversationBusyException"/> class.</summary>
    public ConversationBusyException(string message, Exception innerException)
        : base(ProblemCodes.ConversationBusy, message, innerException)
    {
    }
}
