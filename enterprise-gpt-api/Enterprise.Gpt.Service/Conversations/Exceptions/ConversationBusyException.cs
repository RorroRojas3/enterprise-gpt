namespace Enterprise.Gpt.Service.Exceptions
{
    /// <summary>
    /// Thrown when a chat turn is requested for a conversation that already has one in flight.
    /// Surfaced as HTTP 409 Conflict so callers can distinguish transient contention from a
    /// malformed request and retry once the current turn completes.
    /// </summary>
    public class ConversationBusyException : Exception
    {
        public ConversationBusyException(string message) : base(message) { }

        public ConversationBusyException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
