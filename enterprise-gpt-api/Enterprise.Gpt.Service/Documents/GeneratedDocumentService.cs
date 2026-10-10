using System.Collections.Frozen;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service;

/// <summary>An artifact to store, as it came out of the sandbox.</summary>
/// <param name="FileName">The name the sandbox gave it, used verbatim for the download.</param>
/// <param name="Content">The bytes. Already bounded by the caller's own artifact ceiling.</param>
public sealed record GeneratedDocumentRequest(Guid ConversationId, Guid UserId, string FileName, byte[] Content);

/// <summary>
/// Stores a file this platform produced, without ingesting it.
/// </summary>
public interface IGeneratedDocumentService
{
    /// <summary>Writes an artifact's bytes and the row that points at them.</summary>
    /// <param name="request">The artifact and the conversation it belongs to.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
    /// <returns>The stored document's identity, for the message that introduced it.</returns>
    /// <remarks>
    /// Nothing here extracts, chunks or embeds: a generated document has no chunk rows, which is what
    /// keeps it structurally absent from retrieval rather than filtered out of it.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// The artifact is empty, is larger than <c>Documents:MaxFileSizeBytes</c>, or carries an extension
    /// this platform does not produce. Carries a sentence the caller can relay.
    /// </exception>
    /// <exception cref="NotFoundException">The conversation is gone or was never the caller's.</exception>
    /// <exception cref="StorageNotConfiguredException">The generated-documents container is not configured.</exception>
    Task<MessageAttachmentDto> StoreAsync(GeneratedDocumentRequest request, CancellationToken cancellationToken);

    /// <summary>Takes back a stored document whose turn never delivered it.</summary>
    /// <param name="documentId">The document to withdraw.</param>
    /// <remarks>
    /// For a turn that was stopped or failed after a file had already been stored: the transcript never
    /// references it, so leaving the row active would show the user a file no message introduced. Takes
    /// no cancellation token and never throws — it runs while a turn is already unwinding, and the token
    /// that ended that turn is the one that would abandon this.
    /// </remarks>
    Task DiscardAsync(Guid documentId);
}

/// <inheritdoc />
public sealed class GeneratedDocumentService(
    ILogger<GeneratedDocumentService> logger,
    IBlobStorageService blobStorageService,
    IConfiguration configuration,
    IOptions<DocumentOptions> options,
    IServiceScopeFactory scopeFactory) : IGeneratedDocumentService
{
    private readonly ILogger<GeneratedDocumentService> _logger = logger;
    private readonly IBlobStorageService _blobStorageService = blobStorageService;
    private readonly IConfiguration _configuration = configuration;
    private readonly DocumentOptions _options = options.Value;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// The formats this platform produces. A runaway script writing anything else never becomes a
    /// downloadable blob, whatever the agent's own instructions said.
    /// </summary>
    private static readonly FrozenSet<string> _producibleExtensions =
        new[] { ".csv", ".docx", ".md", ".pdf", ".pptx", ".txt", ".xlsx" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private string GeneratedContainer =>
        DocumentStorage.RequireContainer(_configuration, _logger, DocumentStorage.GeneratedContainerKey);

    /// <inheritdoc />
    public async Task<MessageAttachmentDto> StoreAsync(GeneratedDocumentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fileName = NormalizeFileName(request.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        Validate(request, fileName, extension);

        var documentId = Guid.NewGuid();

        // The same key shape an upload uses. It cannot collide with one: the container differs.
        var blobPath = $"{request.UserId}/{request.ConversationId}/{documentId}{extension}";
        var mimeType = DocumentStorage.ResolveContentType(extension);

        await _blobStorageService.UploadAsync(
            GeneratedContainer,
            blobPath,
            request.Content,
            new Dictionary<string, string>
            {
                ["userId"] = request.UserId.ToString(),
                ["conversationId"] = request.ConversationId.ToString(),
                ["documentId"] = documentId.ToString(),
                ["contentType"] = mimeType,
                ["length"] = request.Content.Length.ToString()
            },
            cancellationToken);

        try
        {
            await InsertAsync(request, documentId, fileName, extension, mimeType, blobPath);
        }
        catch
        {
            // The blob is written first so the bytes survive a slow insert, but a failure past this
            // point leaves content no row references — unreachable, and billed forever.
            await TryDeleteBlobAsync(blobPath, documentId);
            throw;
        }

        _logger.LogInformation(
            "Stored generated document {DocumentId} ({Extension}, {Size} bytes) on conversation {ConversationId}",
            documentId, extension, request.Content.Length, request.ConversationId);

        return new MessageAttachmentDto
        {
            Id = documentId,
            Name = fileName,
            Extension = extension,
            MimeType = mimeType,
            Size = request.Content.Length
        };
    }

    /// <inheritdoc />
    public async Task DiscardAsync(Guid documentId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

            var path = await ctx.ConversationDocuments
                .AsNoTracking()
                .Where(x => x.Id == documentId && x.Type == ConversationDocumentTypes.Generated)
                .Select(x => x.Path)
                .FirstOrDefaultAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (path is null)
            {
                return;
            }

            // The row first: a row pointing at a blob that is gone reads to every listing as a file
            // that will not download, where a blob with no row is merely unreferenced.
            await ctx.ConversationDocuments
                .Where(x => x.Id == documentId && !x.DateDeactivated.HasValue)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(d => d.DateDeactivated, DateTimeOffset.UtcNow), CancellationToken.None)
                .ConfigureAwait(false);

            await TryDeleteBlobAsync(path, documentId).ConfigureAwait(false);

            _logger.LogInformation(
                "Withdrew generated document {DocumentId}; its turn did not deliver it", documentId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never thrown on: this runs inside a turn that is already unwinding, and replacing the
            // exception in flight with a cleanup failure would lose why the turn ended.
            _logger.LogError(
                exception, "Could not withdraw generated document {DocumentId}; it needs reclaiming", documentId);
        }
    }

    /// <summary>
    /// Writes the row on a unit of work of its own.
    /// </summary>
    /// <remarks>
    /// A fresh scope rather than the streaming turn's shared <see cref="EnterpriseGptDbContext"/>, for
    /// the reason <c>DocumentSummaryService.PersistAsync</c> records: EF accepts changes only on a
    /// successful save, so a failure here would leave this row tracked as <c>Added</c> on the turn's
    /// context and re-attempted by the turn's own finalization after the answer had already streamed.
    /// <para>
    /// Uncancellable: by this line the sandbox run has been paid for and the blob is already written,
    /// and the token that would abort the write is the one a client disconnect signals.
    /// </para>
    /// </remarks>
    private async Task InsertAsync(
        GeneratedDocumentRequest request, Guid documentId, string fileName, string extension, string mimeType, string blobPath)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var owned = await ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.ConversationId
                && x.UserId == request.UserId
                && !x.DateDeactivated.HasValue, CancellationToken.None)
            .ConfigureAwait(false);

        if (!owned)
        {
            throw new NotFoundException($"Conversation with id '{request.ConversationId}' was not found.");
        }

        var date = DateTimeOffset.UtcNow;

        ctx.Add(new ConversationDocument
        {
            Id = documentId,
            UserId = request.UserId,
            ConversationId = request.ConversationId,
            Type = ConversationDocumentTypes.Generated,
            Name = fileName,
            Extension = extension,
            MimeType = mimeType,
            Size = request.Content.Length,
            Path = blobPath,
            DateCreated = date,
            DateModified = date
        });

        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // A turn can outlive the conversation it runs on, and the deactivation cascade never re-runs —
        // so without this the insert would leave an active document under a deactivated conversation,
        // which every listing treats as impossible. Set-based, because the cascade may have moved this
        // row's rowversion between the insert and the re-check.
        var stillActive = await ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.ConversationId && !x.DateDeactivated.HasValue, CancellationToken.None)
            .ConfigureAwait(false);

        if (stillActive)
        {
            return;
        }

        await ctx.ConversationDocuments
            .Where(d => d.Id == documentId && !d.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.DateDeactivated, DateTimeOffset.UtcNow), CancellationToken.None)
            .ConfigureAwait(false);

        throw new NotFoundException($"Conversation with id '{request.ConversationId}' was not found.");
    }

    private void Validate(GeneratedDocumentRequest request, string fileName, string extension)
    {
        if (request.Content.Length == 0)
        {
            throw new ValidationException($"'{fileName}' is empty, so there is nothing to save.");
        }

        if (request.Content.Length > _options.MaxFileSizeBytes)
        {
            throw new ValidationException(
                $"'{fileName}' is larger than the {_options.MaxFileSizeBytes / (1024 * 1024)} MB limit for a stored file.");
        }

        if (!_producibleExtensions.Contains(extension))
        {
            throw new ValidationException($"'{fileName}' is not a format this platform stores.");
        }
    }

    /// <summary>Strips any directory the sandbox put on the name, and bounds it to the column.</summary>
    private static string NormalizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());

        return name.Length > 256 ? name[^256..] : name;
    }

    /// <summary>Removes a blob whose row never landed. Never throws — the caller is already failing.</summary>
    private async Task TryDeleteBlobAsync(string blobPath, Guid documentId)
    {
        try
        {
            await _blobStorageService.DeleteAsync(GeneratedContainer, blobPath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(
                exception,
                "Could not remove the orphaned blob for generated document {DocumentId}; it needs reclaiming",
                documentId);
        }
    }
}
