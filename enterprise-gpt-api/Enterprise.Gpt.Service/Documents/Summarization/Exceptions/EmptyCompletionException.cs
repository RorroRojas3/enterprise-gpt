namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a model call succeeds at the transport level but carries no text.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than an <see cref="InvalidOperationException"/> so that a retry policy can
/// single it out. An empty completion is one of the more plausible transient blips from a chat
/// deployment, and treating it as permanent would let one blank response out of a several-hundred
/// call map phase fail a whole document.
/// </para>
/// <para>
/// The permanent variant of "no usable text" — a content filter — arrives as a provider status
/// exception instead, so widening the retry filter to cover this case does not also make a rejection
/// retryable.
/// </para>
/// </remarks>
public class EmptyCompletionException()
    : Exception("The model returned a response with no text.");
