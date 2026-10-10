namespace Enterprise.Gpt.Common.Enums;

/// <summary>
/// The formats a conversation can be exported as.
/// </summary>
/// <remarks>
/// <see cref="Json"/> is a shipped route contract that no client surface offers; it exists for
/// scripted callers. The other four are the ones the download menu draws.
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
