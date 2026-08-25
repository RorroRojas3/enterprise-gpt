namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Settings for document summarization, bound from the <c>Summarization</c> configuration section
/// and validated at startup.
/// </summary>
/// <remarks>
/// The section carries the summarizer's catalog row id and nothing else. Its context window, its
/// provider, its deployment name and both of its prices are read from that <c>Core.Ref.Model</c>
/// row at the moment of use, so an administrator editing the row takes effect without a redeploy —
/// and so a fact about the summarizer can never be stated in two places that disagree.
/// </remarks>
public sealed class SummarizationOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "Summarization";

    /// <summary>
    /// Gets or sets the <c>Core.Ref.Model</c> identifier of the model that performs every
    /// summarization call.
    /// </summary>
    /// <remarks>
    /// Pinned rather than following the conversation's own selected model: summarization is a
    /// compression task, not a reasoning one, and billing it at whatever an expensive deployment
    /// charges would make the feature's cost scale with a choice the user makes for chat.
    /// </remarks>
    public Guid ModelId { get; set; }
}
