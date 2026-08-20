namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// The formats a conversation can be exported as.
/// </summary>
/// <remarks>
/// <see cref="Html"/> and <see cref="Json"/> predate US-1501 and are kept: they are a shipped route
/// contract, and the client simply never offers them. The three US-1501 added are what frame <c>2f</c>
/// draws.
/// </remarks>
public enum ConversationExportFormats
{
    /// <summary>A self-contained HTML document.</summary>
    Html = 1,

    /// <summary>The stored documents' own shape, as JSON.</summary>
    Json = 2,

    /// <summary>CommonMark, carrying each message's text exactly as it was authored.</summary>
    Markdown = 3,

    /// <summary>An Office Open XML word-processing document.</summary>
    Docx = 4,

    /// <summary>A PDF document.</summary>
    Pdf = 5
}

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

/// <summary>
/// Renders a conversation into one export format.
/// </summary>
/// <remarks>
/// One implementation per <see cref="ConversationExportFormats"/> member, resolved by
/// <see cref="Format"/>. A format whose renderer is not registered is reported as
/// <see cref="Exceptions.ExportRendererNotConfiguredException"/> rather than as a 500 — see
/// <c>Program.cs</c>, where PDF registration depends on a usable font being found.
/// </remarks>
public interface IConversationExportRenderer
{
    /// <summary>Gets the format this renderer produces.</summary>
    ConversationExportFormats Format { get; }

    /// <summary>
    /// Renders a conversation.
    /// </summary>
    /// <param name="document">The conversation, already reduced to what an export may contain.</param>
    /// <returns>The rendered document.</returns>
    ConversationExport Render(ConversationExportDocument document);
}
