using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Serialization;
using Enterprise.Gpt.Service.Transcripts;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Repository;

namespace Enterprise.Gpt.Service.Export;

/// <summary>
/// The formats a conversation can be exported as.
/// </summary>
public enum ConversationExportFormats
{
    /// <summary>A self-contained HTML document.</summary>
    Html = 1,

    /// <summary>The stored documents' own shape, as JSON.</summary>
    Json = 2
}

/// <summary>
/// A rendered export, ready to be written to a response.
/// </summary>
/// <param name="Content">The document body.</param>
/// <param name="ContentType">The media type to serve it as.</param>
/// <param name="FileName">A suggested download file name.</param>
public sealed record ConversationExport(string Content, string ContentType, string FileName);

/// <summary>
/// Renders a conversation out of the platform.
/// </summary>
public interface IConversationExportService
{
    /// <summary>
    /// Exports one of the caller's conversations.
    /// </summary>
    /// <param name="id">The conversation to export.</param>
    /// <param name="format">The format to render, defaulting to HTML when not supplied.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The rendered export.</returns>
    /// <exception cref="ValidationException">The requested format is not supported.</exception>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    Task<ConversationExport> ExportConversationAsync(
        Guid id, string? format, CancellationToken cancellationToken = default);
}

/// <summary>
/// Assembles a conversation export from the HTML already stored beside each message.
/// </summary>
/// <remarks>
/// Reads rather than re-renders: the whole point of storing <c>htmlContent</c> at persist time is
/// that an export does not depend on a client, or on this service, rendering markdown again.
/// </remarks>
/// <param name="logger">The logger.</param>
/// <param name="transcriptStore">The transcript store.</param>
/// <param name="tokenService">Supplies the calling user's object identifier.</param>
/// <param name="ctx">The relational context, read for ownership.</param>
public sealed class ConversationExportService(
    ILogger<ConversationExportService> logger,
    ITranscriptStore transcriptStore,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx) : IConversationExportService
{
    private const string MessagesPlaceholder = "{{MESSAGES}}";
    private const string DatePlaceholder = "{{DATE}}";

    /// <summary>
    /// The export template, loaded once at type initialization.
    /// </summary>
    /// <remarks>
    /// Shipped as content beside the assembly rather than embedded, matching how the prompt
    /// templates in this project are loaded.
    /// </remarks>
    private static readonly string HtmlTemplate = LoadTemplate();

    private readonly ILogger<ConversationExportService> _logger = logger;
    private readonly ITranscriptStore _transcriptStore = transcriptStore;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;

    /// <inheritdoc />
    public async Task<ConversationExport> ExportConversationAsync(
        Guid id, string? format, CancellationToken cancellationToken = default)
    {
        var requested = ParseFormat(format);
        var userId = _tokenService.GetOid();

        // Owner-scoped in SQL as well as through the partition key. The key alone would answer
        // correctly, but a deactivated conversation still has a transcript and must not export.
        var exists = await _ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

        if (!exists)
        {
            _logger.LogError("Conversation {Id} not found for export.", id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        var partition = new PartitionKey(PartitionKeys.For(userId, id));

        var header = await _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
            id.ToString(), partition, cancellationToken);

        if (header is null || header.DateDeactivated.HasValue)
        {
            _logger.LogError("Conversation {Id} not found for export.", id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        var messages = await ReadMessagesAsync(id, partition, cancellationToken);

        return requested is ConversationExportFormats.Json
            ? RenderJson(header, messages)
            : RenderHtml(header, messages);
    }

    private async Task<List<TranscriptMessageDocument>> ReadMessagesAsync(
        Guid conversationId, PartitionKey partition, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND c.conversationId = @conversationId ORDER BY c.dateCreated")
            .WithParameter("@type", TranscriptDocumentTypes.Message)
            .WithParameter("@conversationId", conversationId);

        List<TranscriptMessageDocument> messages = [];
        string? continuationToken = null;

        do
        {
            var page = await _transcriptStore.QueryAsync<TranscriptMessageDocument>(
                query, partition, null, continuationToken, cancellationToken);

            messages.AddRange(page.Items);
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        // System messages are the assistant's standing instructions, not part of the conversation a
        // user had, and they are excluded from the read endpoint for the same reason.
        return [.. messages
            .Where(x => x.Role is not ChatRoles.System)
            .OrderBy(x => x.DateCreated)
            .ThenBy(x => x.Id, StringComparer.Ordinal)];
    }

    private static ConversationExport RenderHtml(
        TranscriptHeaderDocument header, IReadOnlyList<TranscriptMessageDocument> messages)
    {
        var body = new StringBuilder();

        foreach (var message in messages)
        {
            var role = message.Role is ChatRoles.User ? "user" : "assistant";
            var label = message.Role is ChatRoles.User ? "You" : "Assistant";

            // Stored HTML when there is any, and escaped plain text when there is not — a message
            // written before server-side rendering existed still belongs in the export.
            var content = string.IsNullOrEmpty(message.HtmlContent)
                ? $"<p>{WebUtility.HtmlEncode(message.Content)}</p>"
                : message.HtmlContent;

            body.Append(CultureInfo.InvariantCulture,
                $"""
                <div class="message {role}">
                    <div class="message-header">{label}</div>
                    <div class="message-content">{content}</div>
                </div>

                """);
        }

        var html = HtmlTemplate
            .Replace(MessagesPlaceholder, body.ToString(), StringComparison.Ordinal)
            .Replace(DatePlaceholder, header.DateModified.ToString("u", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            // The conversation's own name, escaped: it is user-supplied and the template's title is
            // the one place it would otherwise reach markup.
            .Replace("Conversation History", WebUtility.HtmlEncode(header.Name), StringComparison.Ordinal);

        return new ConversationExport(html, "text/html; charset=utf-8", $"{FileStem(header.Name)}.html");
    }

    private static ConversationExport RenderJson(
        TranscriptHeaderDocument header, IReadOnlyList<TranscriptMessageDocument> messages)
    {
        // Serialized with the Cosmos options, so the exported property names are the stored ones and
        // the export is the storage shape rather than a third representation of it.
        var options = CosmosSerialization.CreateOptions();
        options.WriteIndented = true;

        var payload = new
        {
            header.ConversationId,
            header.Name,
            header.DateCreated,
            header.DateModified,
            header.ContextTokens,
            header.MessageCount,
            Messages = messages
        };

        return new ConversationExport(
            JsonSerializer.Serialize(payload, options),
            "application/json; charset=utf-8",
            $"{FileStem(header.Name)}.json");
    }

    private static ConversationExportFormats ParseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            return ConversationExportFormats.Html;
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return ConversationExportFormats.Json;
        }

        throw new ValidationException(
        [
            new ValidationFailure("format", $"'{format}' is not a supported export format. Supported formats are 'html' and 'json'.")
        ]);
    }

    /// <summary>
    /// Reduces a conversation name to something safe to put in a file name.
    /// </summary>
    private static string FileStem(string name)
    {
        var stem = new string([.. name.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')]).Trim('-');

        return string.IsNullOrEmpty(stem) ? "conversation" : stem[..Math.Min(stem.Length, 60)];
    }

    private static string LoadTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Files", "conversation-history.html");

        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"The conversation export template was not found at '{path}'.", path);
    }
}
