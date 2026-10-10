namespace Enterprise.Gpt.Service.Documents.Models;

/// <summary>An artifact to store, as it came out of the sandbox.</summary>
/// <param name="FileName">The name the sandbox gave it, used verbatim for the download.</param>
/// <param name="Content">The bytes. Already bounded by the caller's own artifact ceiling.</param>
public sealed record GeneratedDocumentRequest(Guid ConversationId, Guid UserId, string FileName, byte[] Content);
