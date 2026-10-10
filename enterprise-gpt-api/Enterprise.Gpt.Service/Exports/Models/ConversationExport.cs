using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// A rendered export, ready to be written to a response.
/// </summary>
/// <param name="Content">The document body.</param>
/// <param name="ContentType">The media type to serve it as.</param>
/// <param name="FileName">A suggested download file name.</param>
/// <remarks>
/// Bytes rather than a string, because two of the five formats are binary. A text renderer encodes
/// its own output, which is what keeps the encoding decision beside the format that made it rather
/// than in the endpoint.
/// </remarks>
public sealed record ConversationExport(byte[] Content, string ContentType, string FileName);
