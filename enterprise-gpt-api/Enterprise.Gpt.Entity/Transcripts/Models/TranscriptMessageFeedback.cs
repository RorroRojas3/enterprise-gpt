using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// A reader's verdict on one assistant message.
/// </summary>
/// <remarks>
/// Stamped, not versioned: only the latest change is kept. The first-rated time comes from the
/// relational <c>MessageFeedback</c> row's <c>DateCreated</c>.
/// </remarks>
public sealed record TranscriptMessageFeedback
{
    /// <remarks>
    /// Lowercase here but PascalCase on the HTTP wire, as with <see cref="TranscriptMessageDocument.Role"/>:
    /// only the Cosmos serializer registers a camel-casing string-enum converter.
    /// </remarks>
    [JsonPropertyName("rating")]
    public MessageFeedbackRatings Rating { get; init; }

    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; init; }
}
