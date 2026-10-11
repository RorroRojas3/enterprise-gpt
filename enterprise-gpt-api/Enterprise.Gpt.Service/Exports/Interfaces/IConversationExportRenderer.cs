using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports.Models;

namespace Enterprise.Gpt.Service.Exports.Interfaces;

/// <summary>
/// Renders a conversation into one export format.
/// </summary>
/// <remarks>
/// One implementation per format, resolved by <see cref="Format"/>. An unregistered format (PDF without a
/// usable font) surfaces as <see cref="Exceptions.ExportRendererNotConfiguredException"/>, not a 500.
/// </remarks>
public interface IConversationExportRenderer
{
    ConversationExportFormats Format { get; }

    /// <summary>
    /// Renders a conversation already reduced to what an export may contain.
    /// </summary>
    ConversationExport Render(ConversationExportDocument document);
}
