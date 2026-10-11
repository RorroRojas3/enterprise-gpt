using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Cosmos.Serialization;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Models;

namespace Enterprise.Gpt.Service.Exports.Renderers;

/// <summary>
/// Serializes a conversation as JSON.
/// </summary>
/// <remarks>
/// The contract is the storage shape: <see cref="ConversationExportDocument.Stored"/> written with the
/// Cosmos serializer options, so property names match the stored ones.
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
