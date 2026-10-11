namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A rendered export, ready to be written to a response.
/// </summary>
/// <remarks>
/// Bytes because two formats are binary; text renderers encode their own output, keeping that decision
/// beside the format rather than in the endpoint.
/// </remarks>
public sealed record ConversationExport(byte[] Content, string ContentType, string FileName);
