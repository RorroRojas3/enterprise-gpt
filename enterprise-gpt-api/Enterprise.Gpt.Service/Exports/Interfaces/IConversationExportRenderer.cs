using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports.Models;

namespace Enterprise.Gpt.Service.Exports.Interfaces;

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
