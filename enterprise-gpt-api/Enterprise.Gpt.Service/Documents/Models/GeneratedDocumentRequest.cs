namespace Enterprise.Gpt.Service.Documents.Models;

/// <summary>An artifact to store, as it came out of the sandbox.</summary>
/// <param name="ConversationId">
/// Must belong to <paramref name="UserId"/>, or the store is refused as not found.
/// </param>
/// <param name="UserId">The caller whose turn produced the file.</param>
/// <param name="FileName">The name the sandbox gave it.</param>
/// <param name="Content">Already bounded by the caller's own artifact ceiling.</param>
public sealed record GeneratedDocumentRequest(Guid ConversationId, Guid UserId, string FileName, byte[] Content);
