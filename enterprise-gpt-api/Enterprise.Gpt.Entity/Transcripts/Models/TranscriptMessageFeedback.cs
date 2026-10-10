using System.Text.Json.Serialization;
using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Entity.Transcripts.Models;

/// <summary>
/// A reader's verdict on one assistant message.
/// </summary>
/// <remarks>
/// Stamped rather than versioned: the transcript is append-only for its messages, but a rating is
/// the one thing about a message that a reader may change afterwards, so the moment recorded here
/// is the moment of the latest change and there is no history of the ones before it. The
/// relational <c>MessageFeedback</c> row keeps its own <c>DateCreated</c> beside this, which is
/// where a first-rated-at ever comes from.
/// </remarks>
public sealed record TranscriptMessageFeedback
{
    /// <summary>Gets the verdict.</summary>
    /// <remarks>
    /// Serialized as a lowercase string here and as PascalCase on the HTTP wire, because the Cosmos
    /// serializer registers a camel-casing string-enum converter and the API deliberately does not
    /// — the same split <see cref="TranscriptMessageDocument.Role"/> already carries.
    /// </remarks>
    [JsonPropertyName("rating")]
    public MessageFeedbackRatings Rating { get; init; }

    /// <summary>Gets the moment the rating was last set.</summary>
    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; init; }
}
