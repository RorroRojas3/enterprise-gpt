namespace Enterprise.Gpt.Service.Documents.Summarization.Exceptions;

/// <summary>
/// Thrown when a model call succeeds at the transport level but carries no text.
/// </summary>
/// <remarks>
/// Its own type so the retry policy can treat it as transient; the permanent case, a content filter,
/// arrives as a provider status exception instead.
/// </remarks>
public class EmptyCompletionException()
    : Exception("The model returned a response with no text.");
