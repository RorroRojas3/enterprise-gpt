namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>A file the File Agent may read, and its bytes.</summary>
/// <param name="Id">The conversation document's id, whether the file was uploaded or generated.</param>
/// <param name="Name">The file name as the user knows it, which is also the name in the sandbox.</param>
/// <param name="MimeType">The media type recorded on the document's row, which the sandbox upload declares.</param>
/// <param name="Content">The document's bytes, read from whichever container holds it.</param>
public sealed record FileAgentSourceFile(Guid Id, string Name, string MimeType, byte[] Content);
