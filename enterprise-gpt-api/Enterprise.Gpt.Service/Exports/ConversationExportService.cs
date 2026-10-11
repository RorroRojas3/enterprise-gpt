using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Entity.Transcripts.Models;
using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Exports.Exceptions;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Enterprise.Gpt.Service.Exports.Models;
using Enterprise.Gpt.Service.Security;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Exports;

/// <summary>
/// Renders a conversation out of the platform.
/// </summary>
public interface IConversationExportService
{
    /// <summary>
    /// Exports one of the caller's conversations.
    /// </summary>
    /// <exception cref="ValidationException">The requested format is not a supported one.</exception>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    /// <exception cref="ExportRendererNotConfiguredException">
    /// This deployment has no renderer for the format.
    /// </exception>
    Task<ConversationExport> ExportConversationAsync(
        Guid id, string? format, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a conversation's transcript and hands it to the renderer for the requested format.
/// </summary>
/// <remarks>
/// Owns who may export, what an export may contain and which renderer answers; everything format-specific
/// lives in an <see cref="IConversationExportRenderer"/>.
/// </remarks>
public sealed partial class ConversationExportService(
    ILogger<ConversationExportService> logger,
    ITranscriptStore transcriptStore,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IEnumerable<IConversationExportRenderer> renderers,
    TimeProvider timeProvider) : IConversationExportService
{
    private readonly ILogger<ConversationExportService> _logger = logger;
    private readonly ITranscriptStore _transcriptStore = transcriptStore;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly TimeProvider _timeProvider = timeProvider;

    // Indexer assignment, not ToDictionary: a duplicate key would throw in this scoped constructor and
    // fail every export. Last registration wins, matching how the container resolves a single service.
    private readonly Dictionary<ConversationExportFormats, IConversationExportRenderer> _renderers =
        BuildRegistry(renderers);

    /// <inheritdoc />
    public async Task<ConversationExport> ExportConversationAsync(
        Guid id, string? format, CancellationToken cancellationToken = default)
    {
        var renderer = ResolveRenderer(format);
        var userId = _tokenService.GetOid();

        // Owner-scoped in SQL as well as through the partition key. The key alone would answer
        // correctly, but a deactivated conversation still has a transcript and must not export.
        var exists = await _ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == id && x.UserId == userId, cancellationToken);

        if (!exists)
        {
            LogConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        var partition = new PartitionKey(PartitionKeys.For(userId, id));

        var header = await _transcriptStore.ReadItemAsync<TranscriptHeaderDocument>(
            id.ToString(), partition, cancellationToken);

        if (header is null || header.DateDeactivated.HasValue)
        {
            LogConversationNotFound(_logger, id);
            throw new NotFoundException($"Conversation {id} not found.");
        }

        var messages = await ReadMessagesAsync(id, partition, cancellationToken);

        // Rendering is synchronous and can be the costliest step (a PDF lays out every page here), so a
        // caller who already left gets a 499 rather than wasted work.
        cancellationToken.ThrowIfCancellationRequested();

        return renderer.Render(
            new ConversationExportDocument(header, messages, _timeProvider.GetUtcNow()));
    }

    #region Private methods

    private static Dictionary<ConversationExportFormats, IConversationExportRenderer> BuildRegistry(
        IEnumerable<IConversationExportRenderer> renderers)
    {
        Dictionary<ConversationExportFormats, IConversationExportRenderer> registry = [];

        foreach (var renderer in renderers)
        {
            registry[renderer.Format] = renderer;
        }

        return registry;
    }

    private IConversationExportRenderer ResolveRenderer(string? format)
    {
        if (!ConversationExportFormatNames.TryParse(format, out var requested))
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    "format",
                    $"'{format}' is not a supported export format. Supported formats are {ConversationExportFormatNames.SupportedList}.")
            ]);
        }

        if (_renderers.TryGetValue(requested, out var renderer))
        {
            return renderer;
        }

        // Info rather than Error: the deployment is configured this way on purpose more often than
        // not, and a request for a format an operator withdrew is not a fault to page anyone about.
        var wireFormat = requested.ToWireFormat();
        LogNoExportRenderer(_logger, wireFormat);

        throw new ExportRendererNotConfiguredException(requested);
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

        // An allowlist: downstream labels are only You or Assistant, so anything else, such as a Tool
        // document, would export as an assistant answer it never was.
        return [.. messages
            .Where(x => x.Role is ChatRoles.User or ChatRoles.Assistant)
            .OrderBy(x => x.DateCreated)
            .ThenBy(x => x.Id, StringComparer.Ordinal)];
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation {Id} not found for export.")]
    private static partial void LogConversationNotFound(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation export to {Format} was requested, but no renderer for it is registered.")]
    private static partial void LogNoExportRenderer(ILogger logger, string format);

    #endregion
}
