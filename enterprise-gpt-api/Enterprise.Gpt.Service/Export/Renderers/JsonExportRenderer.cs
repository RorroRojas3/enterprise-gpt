using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Service.Serialization;

namespace Enterprise.Gpt.Service.Export.Renderers;

/// <summary>
/// Serializes a conversation as JSON.
/// </summary>
/// <remarks>
/// The stored documents, written with the Cosmos serializer options — so the exported property names
/// are the stored ones and the export is the storage shape rather than a third representation of it.
/// That is the whole contract of this format, which is why it is the one renderer that reads
/// <see cref="ConversationExportDocument.Stored"/> instead of the reduced message view.
/// </remarks>
public sealed class JsonExportRenderer : IConversationExportRenderer
{
    /// <inheritdoc />
    public ConversationExportFormats Format => ConversationExportFormats.Json;

    /// <inheritdoc />
    public ConversationExport Render(ConversationExportDocument document)
    {
        var options = CosmosSerialization.CreateOptions();
        options.WriteIndented = true;

        var payload = new
        {
            document.Header.ConversationId,
            document.Header.Name,
            document.Header.DateCreated,
            document.Header.DateModified,
            document.Header.ContextTokens,
            document.Header.MessageCount,
            Messages = document.Stored
        };

        return new ConversationExport(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, options)),
            "application/json; charset=utf-8",
            $"{ExportFileNames.Stem(document.Name)}.json");
    }
}
