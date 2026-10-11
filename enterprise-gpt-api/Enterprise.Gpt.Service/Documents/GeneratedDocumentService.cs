using System.Collections.Frozen;
using System.Globalization;
using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Blob;
using Enterprise.Gpt.Repository.Blob.Options;
using Enterprise.Gpt.Repository.Sql.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Exceptions;
using Enterprise.Gpt.Service.Documents.Models;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Options;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents;

/// <summary>
/// Stores a file this platform produced, without ingesting it.
/// </summary>
public interface IGeneratedDocumentService
{
    /// <summary>Writes an artifact's bytes and the row that points at them.</summary>
    /// <remarks>
    /// Never chunked, so a generated document is structurally absent from retrieval rather than filtered out.
    /// </remarks>
    /// <exception cref="ValidationException">
    /// The artifact is empty, over <c>Documents:MaxFileSizeBytes</c>, or not a producible format; the message
    /// is safe to relay.
    /// </exception>
    /// <exception cref="NotFoundException">The conversation is gone or was never the caller's.</exception>
    /// <exception cref="StorageNotConfiguredException">The generated-documents container is not configured.</exception>
    Task<MessageAttachmentDto> StoreAsync(GeneratedDocumentRequest request, CancellationToken cancellationToken);

    /// <summary>Takes back a stored document whose turn never delivered it.</summary>
    /// <remarks>
    /// Takes no token and never throws: it runs while the turn unwinds, usually because that token fired.
    /// </remarks>
    Task DiscardAsync(Guid documentId);
}

/// <inheritdoc />
public sealed partial class GeneratedDocumentService(
    ILogger<GeneratedDocumentService> logger,
    IDocumentStore documentStore,
    IOptionsMonitor<BlobStorageOptions> storageOptions,
    IOptions<DocumentOptions> options,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IGeneratedDocumentService
{
    private readonly ILogger<GeneratedDocumentService> _logger = logger;
    private readonly IDocumentStore _documentStore = documentStore;
    private readonly IOptionsMonitor<BlobStorageOptions> _storageOptions = storageOptions;
    private readonly DocumentOptions _options = options.Value;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;

    // A runaway script writing any other format never becomes a downloadable blob.
    private static readonly FrozenSet<string> _producibleExtensions =
        new[] { ".csv", ".docx", ".md", ".pdf", ".pptx", ".txt", ".xlsx" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private string GeneratedContainer =>
        DocumentStorage.RequireGeneratedContainer(_storageOptions.CurrentValue, _logger);

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

        await _documentStore.UploadAsync(
            GeneratedContainer,
            blobPath,
            request.Content,
            new Dictionary<string, string>
            {
                ["userId"] = request.UserId.ToString(),
                ["conversationId"] = request.ConversationId.ToString(),
                ["documentId"] = documentId.ToString(),
                ["contentType"] = mimeType,
                ["length"] = request.Content.Length.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);

        try
        {
            await InsertAsync(request, documentId, fileName, extension, mimeType, blobPath);
        }
        catch
        {
            // A failed insert would otherwise leave the bytes unreferenced and billed forever.
            await TryDeleteBlobAsync(blobPath, documentId);
            throw;
        }

        LogGeneratedDocumentStored(_logger, documentId, extension, request.Content.Length, request.ConversationId);

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

            var path = await FindGeneratedPathIncludingDeactivatedAsync(ctx, documentId).ConfigureAwait(false);

            if (path is null)
            {
                return;
            }

            // Row first: a row over a missing blob lists as a file that will not download, whereas a
            // rowless blob is merely unreferenced.
            await ctx.ConversationDocuments
                .Where(x => x.Id == documentId)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(d => d.DateDeactivated, _timeProvider.GetUtcNow()), CancellationToken.None)
                .ConfigureAwait(false);

            await TryDeleteBlobAsync(path, documentId).ConfigureAwait(false);

            LogGeneratedDocumentWithdrawn(_logger, documentId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The turn is already unwinding; a cleanup failure must not replace why it ended.
            LogGeneratedDocumentWithdrawFailed(_logger, exception, documentId);
        }
    }

    #region Private methods

    // Own scope: on the turn's shared context a failed save would leave the row Added and retried by the
    // turn's finalization. Uncancellable: the blob is written, and the token is the client-disconnect one.
    private async Task InsertAsync(
        GeneratedDocumentRequest request, Guid documentId, string fileName, string extension, string mimeType, string blobPath)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var owned = await ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.ConversationId
                && x.UserId == request.UserId, CancellationToken.None)
            .ConfigureAwait(false);

        if (!owned)
        {
            throw new NotFoundException($"Conversation with id '{request.ConversationId}' was not found.");
        }

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
            Path = blobPath
        });

        await ctx.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // A turn can outlive its conversation, whose deactivation cascade never re-runs. Set-based below,
        // because the cascade may have moved this row's rowversion.
        var stillActive = await ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.ConversationId, CancellationToken.None)
            .ConfigureAwait(false);

        if (stillActive)
        {
            return;
        }

        await ctx.ConversationDocuments
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.DateDeactivated, _timeProvider.GetUtcNow()), CancellationToken.None)
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

    private static string NormalizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());

        return name.Length > DocumentLimits.NameMaxLength ? name[^DocumentLimits.NameMaxLength..] : name;
    }

    // A cascade may already have deactivated the row, and its blob still has to go.
    private static Task<string?> FindGeneratedPathIncludingDeactivatedAsync(EnterpriseGptDbContext ctx, Guid documentId) =>
        ctx.ConversationDocuments
            .IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .AsNoTracking()
            .Where(x => x.Id == documentId && x.Type == ConversationDocumentTypes.Generated)
            .Select(x => x.Path)
            .FirstOrDefaultAsync(CancellationToken.None);

    private async Task TryDeleteBlobAsync(string blobPath, Guid documentId)
    {
        try
        {
            await _documentStore.DeleteAsync(GeneratedContainer, blobPath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogOrphanedBlobRemoveFailed(_logger, exception, documentId);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored generated document {DocumentId} ({Extension}, {Size} bytes) on conversation {ConversationId}")]
    private static partial void LogGeneratedDocumentStored(
        ILogger logger, Guid documentId, string extension, int size, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Withdrew generated document {DocumentId}; its turn did not deliver it")]
    private static partial void LogGeneratedDocumentWithdrawn(ILogger logger, Guid documentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not withdraw generated document {DocumentId}; it needs reclaiming")]
    private static partial void LogGeneratedDocumentWithdrawFailed(
        ILogger logger, Exception exception, Guid documentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not remove the orphaned blob for generated document {DocumentId}; it needs reclaiming")]
    private static partial void LogOrphanedBlobRemoveFailed(ILogger logger, Exception exception, Guid documentId);

    #endregion
}
