using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Transcripts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Transcripts;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Export;

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
    /// <exception cref="ValidationException">The requested format is not a supported one.</exception>
    /// <exception cref="NotFoundException">The conversation is not an active conversation of the caller.</exception>
    /// <exception cref="ExportRendererNotConfiguredException">
    /// The format is supported but this deployment has no renderer for it.
    /// </exception>
    Task<ConversationExport> ExportConversationAsync(
        Guid id, string? format, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a conversation's transcript and hands it to the renderer for the requested format.
/// </summary>
/// <remarks>
/// <para>
/// The service owns three things and no more: who may export (SQL ownership plus a live transcript
/// header), what an export may contain (the persisted prompts and answers, and nothing else),
/// and which renderer answers. Everything about a particular format lives in its
/// <see cref="IConversationExportRenderer"/>, so adding one is a registration rather than a branch
/// here.
/// </para>
/// <para>
/// A format with no registered renderer raises <see cref="ExportRendererNotConfiguredException"/>
/// rather than falling through to a 500. That is not a defensive nicety: PDF rendering depends on a
/// font this deployment may not have, and <c>Export:DisabledFormats</c> can withdraw any of them, so
/// "supported by this API but not available here" is a state a client has to be able to tell apart
/// from a transient failure.
/// </para>
/// </remarks>
/// <param name="logger">The logger.</param>
/// <param name="transcriptStore">The transcript store.</param>
/// <param name="tokenService">Supplies the calling user's object identifier.</param>
/// <param name="ctx">The relational context, read for ownership.</param>
/// <param name="renderers">Every export renderer this deployment registered.</param>
/// <param name="timeProvider">Supplies the export timestamp.</param>
public sealed class ConversationExportService(
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

    // Indexer assignment, not ToDictionary: the latter throws on a duplicate key, and the throw would
    // land in this scoped service's constructor on every export request — surfacing as a 400 with a
    // LINQ message in the body, because ArgumentException is the handler's bad-request arm. Last
    // registration wins instead, matching how the container resolves a single service, so a
    // deployment overriding one format's renderer does not have to remove the original first.
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

        // Rendering is synchronous and can be the most expensive part of the request — a PDF lays out
        // every page on this thread. A caller who navigated away has already stopped waiting, and
        // OperationCanceledException is a 499 with no body rather than wasted work.
        cancellationToken.ThrowIfCancellationRequested();

        return renderer.Render(
            new ConversationExportDocument(header, messages, _timeProvider.GetUtcNow()));
    }

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
        _logger.LogInformation(
            "Conversation export to {Format} was requested, but no renderer for it is registered.",
            requested.ToWireFormat());

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

        // An allowlist rather than "everything except System": the export's contract is the prompts
        // and the completed answers, and the label a message gets downstream is a two-way choice
        // between You and Assistant. A Tool document — the enum has the member, nothing writes one
        // today — would otherwise be exported as an assistant answer it never was.
        return [.. messages
            .Where(x => x.Role is ChatRoles.User or ChatRoles.Assistant)
            .OrderBy(x => x.DateCreated)
            .ThenBy(x => x.Id, StringComparer.Ordinal)];
    }
}
