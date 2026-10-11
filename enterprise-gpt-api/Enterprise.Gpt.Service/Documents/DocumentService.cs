using System.Diagnostics;
using System.Globalization;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Repository.Blob;
using Enterprise.Gpt.Repository.Blob.Options;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.BackgroundJobs.Constants;
using Enterprise.Gpt.Service.BackgroundJobs.Exceptions;
using Enterprise.Gpt.Service.BackgroundJobs.Models;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Exceptions;
using Enterprise.Gpt.Service.Documents.Extraction;
using Enterprise.Gpt.Service.Documents.Extraction.Interfaces;
using Enterprise.Gpt.Service.Documents.Extraction.Models;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Options;
using Enterprise.Gpt.Service.Security;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents;

public interface IDocumentService
{
    /// <summary>
    /// Validates an uploaded file and queues it for ingestion into a conversation.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The file is empty, too large, or unsupported.</exception>
    /// <exception cref="NotFoundException">The conversation is missing or another user's.</exception>
    Task<JobDto> QueueConversationDocumentAsync(Guid conversationId, FileDto file, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the ingestion pipeline for a queued upload: store, extract, chunk, embed, persist.
    /// </summary>
    /// <remarks>
    /// Runs on a <see cref="BackgroundJobProcessor"/> scope, so <paramref name="userId"/> is passed in rather
    /// than read from the current principal.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is null or whitespace.</exception>
    /// <exception cref="ValidationException">The document contains no readable text.</exception>
    Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto file, Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Validates an uploaded file and queues it for ingestion into a project.
    /// </summary>
    /// <remarks>
    /// Shared by every conversation in the project; a conversation upload never adds to the project.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The file is empty, too large, or unsupported.</exception>
    /// <exception cref="NotFoundException">The project does not exist or belongs to another user.</exception>
    Task<JobDto> QueueProjectDocumentAsync(Guid projectId, FileDto file, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the ingestion pipeline for a queued project upload: store, extract, chunk, embed, persist.
    /// </summary>
    /// <remarks>
    /// Runs on a <see cref="BackgroundJobProcessor"/> scope, so <paramref name="userId"/> is passed in rather
    /// than read from the current principal.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="jobId"/> is null or whitespace.</exception>
    /// <exception cref="ValidationException">The document contains no readable text.</exception>
    Task<ProjectDocumentDto> CreateProjectDocumentAsync(string jobId, FileDto file, Guid userId, Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a short-lived link to the original file behind a conversation document.
    /// </summary>
    /// <remarks>
    /// Storage serves the bytes itself; see <see cref="DocumentDownloadDto"/> for the link's confidentiality.
    /// </remarks>
    /// <exception cref="NotFoundException">
    /// The document does not exist, is deactivated, or belongs to a conversation the caller does not own.
    /// </exception>
    /// <exception cref="StorageNotConfiguredException">Storage cannot sign a link.</exception>
    Task<DocumentDownloadDto> GetConversationDocumentDownloadAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a short-lived link to the original file behind a project document.
    /// </summary>
    /// <remarks>
    /// Storage serves the bytes itself; see <see cref="DocumentDownloadDto"/> for the link's confidentiality.
    /// </remarks>
    /// <exception cref="NotFoundException">
    /// The document does not exist, is deactivated, or belongs to a project the caller does not own.
    /// </exception>
    /// <exception cref="StorageNotConfiguredException">Storage cannot sign a link.</exception>
    Task<DocumentDownloadDto> GetProjectDocumentDownloadAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the caller's active conversation documents across every conversation, newest first,
    /// as one page for the documents library.
    /// </summary>
    /// <remarks>
    /// Paging is clamped (<paramref name="take"/> to 1..100), not rejected. <paramref name="type"/> narrows by
    /// origin and <paramref name="name"/> by case-insensitive substring; blank narrows nothing.
    /// </remarks>
    /// <exception cref="ValidationException"><paramref name="type"/> names no supported origin.</exception>
    Task<PaginatedResponseDto<UserDocumentDto>> GetUserDocumentsAsync(int skip = 0, int take = 20, string? type = null, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes one uploaded document of one of the caller's conversations, together with its
    /// summary, sheets and chunks.
    /// </summary>
    /// <remarks>
    /// The blob is left for a retention job to reclaim.
    /// </remarks>
    /// <exception cref="NotFoundException">
    /// The conversation or document does not exist, is deactivated, is generated rather than uploaded, or is
    /// not the caller's.
    /// </exception>
    Task DeactivateConversationDocumentAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued or running upload and removes whatever it already produced.
    /// </summary>
    /// <remarks>
    /// Idempotent: a succeeded job has its document soft-deleted; a failed or cancelled one is left alone.
    /// </remarks>
    /// <exception cref="NotFoundException">No such job exists, or it belongs to another user.</exception>
    Task CancelUploadAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates document ingestion: sequencing, persistence and progress. Reading is delegated to
/// <see cref="IDocumentTextExtractor"/> and splitting to <see cref="ITextChunker"/>.
/// </summary>
/// <remarks>
/// Conversation and project uploads share one pipeline, differing only in the ownership check, blob key
/// and entity.
/// </remarks>
public partial class DocumentService(ILogger<DocumentService> logger,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IDocumentStore documentStore,
    IOptionsMonitor<BlobStorageOptions> storageOptions,
    IOptions<DocumentOptions> options,
    IDocumentTextExtractorFactory extractorFactory,
    ITextChunker textChunker,
    IValidator<FileDto> fileValidator,
    IBackgroundJobQueue backgroundJobQueue,
    IJobStatusStore jobStatusStore,
    IJobCancellationRegistry jobCancellationRegistry,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    TimeProvider timeProvider) : IDocumentService
{
    // Progress bands sized by stage duration; embedding is widest as the longest and only countable stage.
    private const int UploadStart = 0;
    private const int ExtractStart = 10;
    private const int ChunkStart = 45;
    private const int EmbedStart = 50;
    private const int PersistStart = 92;
    private readonly ILogger<DocumentService> _logger = logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
    private readonly IDocumentStore _documentStore = documentStore;
    private readonly IOptionsMonitor<BlobStorageOptions> _storageOptions = storageOptions;
    private readonly DocumentOptions _options = options.Value;
    private readonly IDocumentTextExtractorFactory _extractorFactory = extractorFactory;
    private readonly ITextChunker _textChunker = textChunker;
    private readonly IValidator<FileDto> _fileValidator = fileValidator;
    private readonly IBackgroundJobQueue _backgroundJobQueue = backgroundJobQueue;
    private readonly IJobStatusStore _jobStatusStore = jobStatusStore;
    private readonly IJobCancellationRegistry _jobCancellationRegistry = jobCancellationRegistry;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly TimeProvider _timeProvider = timeProvider;

    private string DocumentsContainer =>
        DocumentStorage.RequireDocumentsContainer(_storageOptions.CurrentValue, _logger);

    // Its own container, not a prefix in the uploads one, so generated and uploaded files never share
    // retention, access or lifecycle policy.
    private string GeneratedContainer =>
        DocumentStorage.RequireGeneratedContainer(_storageOptions.CurrentValue, _logger);

    /// <inheritdoc />
    public async Task<JobDto> QueueConversationDocumentAsync(Guid conversationId, FileDto file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        await _fileValidator.ValidateAndThrowAsync(file, cancellationToken);

        // Resolved here, on the request thread: the background scope has no HttpContext to read it from.
        var userId = _tokenService.GetOid();

        var conversationExists = await _ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken);

        if (!conversationExists)
        {
            // Indistinguishable from "does not exist" so other users' conversation ids cannot be probed.
            LogConversationUploadRejected(_logger, conversationId, userId);
            throw new NotFoundException($"Conversation with id '{conversationId}' was not found.");
        }

        var job = await EnqueueAsync(
            userId,
            new JobTarget(JobTargetKinds.Conversation, conversationId),
            (documentService, jobId, token) => documentService.CreateConversationDocumentAsync(jobId, file, userId, conversationId, token),
            cancellationToken);

        LogConversationUploadQueued(_logger, job.Id, conversationId, userId);

        return job;
    }

    /// <inheritdoc />
    public async Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto file, Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(file);

        LogConversationIngestionStarted(_logger, jobId, conversationId, file.FileExtension, file.Length);

        var documentId = Guid.NewGuid();
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        // Keyed by document id, not file name: same-name uploads into one conversation must not collide.
        var blobPath = $"{userId}/{conversationId}/{documentId}{extension}";

        try
        {
            var ingestion = await IngestAsync(jobId, file, blobPath,
                BuildBlobMetadata(file, userId, documentId, "conversationId", conversationId), cancellationToken);

            _jobStatusStore.Report(jobId, JobStatuses.Persisting, PersistStart, $"Saving {ingestion.Chunks.Count} chunk(s)");

            var document = new ConversationDocument
            {
                Id = documentId,
                UserId = userId,
                ConversationId = conversationId,
                Name = file.FileName,
                Extension = extension,
                MimeType = file.ContentType,
                Size = file.Length,
                Path = blobPath,
                Chunks = BuildChunks<ConversationDocumentChunk>(ingestion),
                Sheets = BuildSheets<ConversationDocumentSheet, ConversationDocumentSheetColumn, ConversationDocumentSheetRow>(
                    ingestion, sheet => sheet.Columns, sheet => sheet.Rows)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            // A conversation deactivated mid-ingestion has already run its one-shot document cascade, so
            // re-check after the save rather than leave an active document under it.
            var conversationStillActive = await _ctx.Conversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == conversationId, cancellationToken);

            if (!conversationStillActive)
            {
                LogConversationDeactivatedDuringIngestion(_logger, conversationId, jobId, documentId);
                throw new NotFoundException($"Conversation with id '{conversationId}' was not found.");
            }

            // Ordering point against a concurrent cancel: the first terminal snapshot wins and the loser
            // cleans up. The database cannot arbitrate: a delete before this insert commits matches nothing.
            if (!_jobStatusStore.Complete(jobId, documentId, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed"))
            {
                throw new JobCancelledException($"Upload job {jobId} was cancelled.");
            }

            LogIngestionCompleted(_logger, jobId, documentId, ingestion.Chunks.Count);

            return document.MapToConversationDocumentDto();
        }
        catch
        {
            await TryCleanUpAsync(JobTargetKinds.Conversation, documentId, blobPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<JobDto> QueueProjectDocumentAsync(Guid projectId, FileDto file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        await _fileValidator.ValidateAndThrowAsync(file, cancellationToken);

        var userId = _tokenService.GetOid();

        var projectExists = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.Id == projectId && x.UserId == userId, cancellationToken);

        if (!projectExists)
        {
            LogProjectUploadRejected(_logger, projectId, userId);
            throw new NotFoundException($"Project with id '{projectId}' was not found.");
        }

        var job = await EnqueueAsync(
            userId,
            new JobTarget(JobTargetKinds.Project, projectId),
            (documentService, jobId, token) => documentService.CreateProjectDocumentAsync(jobId, file, userId, projectId, token),
            cancellationToken);

        LogProjectUploadQueued(_logger, job.Id, projectId, userId);

        return job;
    }

    /// <inheritdoc />
    public async Task<ProjectDocumentDto> CreateProjectDocumentAsync(string jobId, FileDto file, Guid userId, Guid projectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(file);

        LogProjectIngestionStarted(_logger, jobId, projectId, file.FileExtension, file.Length);

        var documentId = Guid.NewGuid();
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        // The "projects" segment keeps this from ever colliding with a "{userId}/{conversationId}/..." key.
        var blobPath = $"{userId}/projects/{projectId}/{documentId}{extension}";

        try
        {
            var ingestion = await IngestAsync(jobId, file, blobPath,
                BuildBlobMetadata(file, userId, documentId, "projectId", projectId), cancellationToken);

            _jobStatusStore.Report(jobId, JobStatuses.Persisting, PersistStart, $"Saving {ingestion.Chunks.Count} chunk(s)");

            var document = new ProjectDocument
            {
                Id = documentId,
                UserId = userId,
                ProjectId = projectId,
                Name = file.FileName,
                Extension = extension,
                MimeType = file.ContentType,
                Size = file.Length,
                Path = blobPath,
                Chunks = BuildChunks<ProjectDocumentChunk>(ingestion),
                Sheets = BuildSheets<ProjectDocumentSheet, ProjectDocumentSheetColumn, ProjectDocumentSheetRow>(
                    ingestion, sheet => sheet.Columns, sheet => sheet.Rows)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            // Same mid-ingest deactivation window as the conversation path.
            var projectStillActive = await _ctx.Projects
                .AsNoTracking()
                .AnyAsync(x => x.Id == projectId, cancellationToken);

            if (!projectStillActive)
            {
                LogProjectDeactivatedDuringIngestion(_logger, projectId, jobId, documentId);
                throw new NotFoundException($"Project with id '{projectId}' was not found.");
            }

            if (!_jobStatusStore.Complete(jobId, documentId, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed"))
            {
                throw new JobCancelledException($"Upload job {jobId} was cancelled.");
            }

            LogIngestionCompleted(_logger, jobId, documentId, ingestion.Chunks.Count);

            return document.MapToProjectDocumentDto();
        }
        catch
        {
            await TryCleanUpAsync(JobTargetKinds.Project, documentId, blobPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DocumentDownloadDto> GetConversationDocumentDownloadAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken)
    {
        var userId = _tokenService.GetOid();

        // Ownership comes off the conversation, not ConversationDocument.UserId, so the download rule
        // is the same one that made the document visible.
        var document = await _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(x => x.Id == documentId
                && x.ConversationId == conversationId
                && x.Conversation.UserId == userId)
            .Select(x => new DocumentBlobRef(x.Name, x.Extension, x.Path, x.Type == ConversationDocumentTypes.Generated))
            .FirstOrDefaultAsync(cancellationToken);

        return BuildDownload(document, documentId, userId);
    }

    /// <inheritdoc />
    public async Task<DocumentDownloadDto> GetProjectDocumentDownloadAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken)
    {
        var userId = _tokenService.GetOid();

        var document = await _ctx.ProjectDocuments
            .AsNoTracking()
            .Where(x => x.Id == documentId
                && x.ProjectId == projectId
                && x.Project.UserId == userId
                && !x.Project.DateDeactivated.HasValue)
            .Select(x => new DocumentBlobRef(x.Name, x.Extension, x.Path, false))
            .FirstOrDefaultAsync(cancellationToken);

        return BuildDownload(document, documentId, userId);
    }

    /// <inheritdoc />
    public async Task<PaginatedResponseDto<UserDocumentDto>> GetUserDocumentsAsync(int skip = 0, int take = 20, string? type = null, string? name = null, CancellationToken cancellationToken = default)
    {
        // Resolved first, so a misspelled origin fails before any database work.
        var origin = DocumentTypeNames.Resolve(type);

        // Clamped, not validated: these come off the query string, and take = 0 would divide by zero below.
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);

        var userId = _tokenService.GetOid();

        // No conversation-active predicate: deactivation cascades to documents and both write paths
        // re-check the conversation after persisting, so an active document implies an active conversation.
        var query = _ctx.ConversationDocuments
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        if (origin.HasValue)
        {
            query = query.Where(x => x.Type == origin.Value);
        }

        // Before the count so TotalCount is filtered; server-side so it reaches past the loaded page.
        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => EF.Functions.Like(x.Name, $"%{name}%"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            // Id breaks DateCreated ties so same-tick rows cannot straddle a page boundary.
            .OrderByDescending(x => x.DateCreated)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(ConversationDocumentMapper.MapToUserDocumentDtoExpression(origin, name))
            .ToListAsync(cancellationToken);

        return new PaginatedResponseDto<UserDocumentDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageSize = take,
            CurrentPage = (skip / take) + 1
        };
    }

    /// <inheritdoc />
    public async Task DeactivateConversationDocumentAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();

        var conversationExists = await _ctx.Conversations
            .AsNoTracking()
            .AnyAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken);

        if (!conversationExists)
        {
            LogDeleteRejected(_logger, conversationId, userId);
            throw new NotFoundException($"Conversation with id '{conversationId}' was not found.");
        }

        // Uploaded only: a generated file is also referenced from the Cosmos transcript, which nothing here
        // patches, so deleting it would leave a chip that 404s.
        var exists = await _ctx.ConversationDocuments
            .AsNoTracking()
            .AnyAsync(x => x.Id == documentId
                && x.ConversationId == conversationId
                && x.Type == ConversationDocumentTypes.Uploaded, cancellationToken);

        if (!exists)
        {
            LogDocumentNotFoundInConversation(_logger, documentId, conversationId, userId);
            throw new NotFoundException($"Document with id '{documentId}' was not found.");
        }

        await DeactivateConversationDocumentRowsAsync(documentId, cancellationToken);

        // Blob deliberately left for a retention job, which also reaches blobs orphaned by failed ingestion.
        LogConversationDocumentDeactivated(_logger, documentId, userId);
    }

    /// <inheritdoc />
    public async Task CancelUploadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        var snapshot = _jobStatusStore.Get(jobId);

        // The status route's rule exactly, so unknown and not-yours look alike.
        if (snapshot is null || snapshot.UserId != userId)
        {
            LogCancelRejected(_logger, jobId, userId);
            throw new NotFoundException("Upload job not found.");
        }

        // Before the signal, deliberately: this write orders against the worker's completion, so either it
        // claims the terminal slot or the returned snapshot carries the document to remove.
        var outcome = _jobStatusStore.Cancel(jobId, JobMessages.Cancelled) ?? snapshot;

        await _jobCancellationRegistry.CancelAsync(jobId);

        if (outcome.DocumentId is { } documentId && outcome.Target is { } target)
        {
            // None: the terminal state is already published, so a caller hanging up must not leave the
            // document behind. The blob stays, as for any delete.
            await DeactivateDocumentRowsAsync(target.Kind, documentId, CancellationToken.None);
        }

        LogUploadJobCancelled(_logger, jobId, userId);
    }

    // Projected so no chunk or embedding data is read. IsGenerated comes off the row because both
    // containers hold the same formats.
    private sealed record DocumentBlobRef(string Name, string Extension, string Path, bool IsGenerated);

    // Embeddings align positionally with Chunks.
    private sealed record IngestionResult(
        IReadOnlyList<TextChunkDto> Chunks,
        IReadOnlyList<ReadOnlyMemory<float>> Embeddings,
        IReadOnlyList<SheetStructureDto> Sheets);

    // Progress<T> posts to the thread pool, so a stale report could overwrite a newer one; reporting
    // inline keeps the status feed ordered.
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Action<T> _handler = handler;

        public void Report(T value) => _handler(value);
    }

    #region Private methods

    private DocumentDownloadDto BuildDownload(DocumentBlobRef? document, Guid documentId, Guid userId)
    {
        if (document is null)
        {
            // One 404 for every way the query can miss, so document ids cannot be probed.
            LogDownloadRejected(_logger, documentId, userId);
            throw new NotFoundException($"Document with id '{documentId}' was not found.");
        }

        var lifetime = TimeSpan.FromMinutes(_options.DownloadUrlLifetimeMinutes);

        // Recomputed: the SAS expiry is not exposed, and both come from the same clock.
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);

        var sasUri = _documentStore.GenerateReadUri(
            document.IsGenerated ? GeneratedContainer : DocumentsContainer,
            document.Path,
            lifetime,
            BuildContentDisposition(document.Name),
            DocumentStorage.ResolveContentType(document.Extension))
            ?? throw new StorageNotConfiguredException();

        // The link is a bearer credential until it expires, so only the document id is recorded.
        LogDownloadLinkIssued(_logger, documentId, userId, expiresAt);

        return new DocumentDownloadDto
        {
            DownloadUrl = sasUri.ToString(),
            FileName = document.Name,
            ExpiresAt = expiresAt
        };
    }

    // RFC 6266: filename* carries the real (possibly non-ASCII) name. The quoted fallback is not
    // percent-encoded, so quotes, backslashes and control characters are stripped from it.
    private static string BuildContentDisposition(string fileName)
    {
        var fallback = string.Concat(fileName.Where(c =>
            c is >= ' ' and <= '~' and not '"' and not '\\' and not '/' and not ':'));

        if (fallback.Length == 0)
        {
            fallback = "download";
        }

        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private async Task<JobDto> EnqueueAsync(
        Guid userId,
        JobTarget target,
        Func<IDocumentService, string, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid().ToString();
        _jobStatusStore.Register(jobId, userId, target);

        try
        {
            // Before enqueueing: the bounded queue can block, and a caller who can poll must also cancel.
            _jobCancellationRegistry.Register(jobId);

            await _backgroundJobQueue.EnqueueAsync(new JobWorkItem(
                jobId,
                async (serviceProvider, token) =>
                {
                    var documentService = serviceProvider.GetRequiredService<IDocumentService>();
                    await work(documentService, jobId, token);
                }), cancellationToken);
        }
        catch
        {
            // Fail the snapshot or it sits in Queued forever (only terminal states are evicted). Released
            // first so a registration never outlives its job.
            _jobCancellationRegistry.Release(jobId);
            _jobStatusStore.Fail(jobId, "The upload could not be queued for processing. Please try again.");
            throw;
        }

        return new JobDto { Id = jobId };
    }

    private async Task<IngestionResult> IngestAsync(
        string jobId, FileDto file, string blobPath,
        Dictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        // Cleanup is the caller's: one boundary around the whole write path also covers a failed persist
        // and a store cancelled partway.
        await StoreAsync(jobId, file, blobPath, metadata, cancellationToken);

        var extraction = await ExtractAsync(jobId, file, cancellationToken);
        var chunks = ChunkText(jobId, extraction.Segments, cancellationToken);
        var embeddings = await EmbedAsync(jobId, chunks, cancellationToken);

        return new IngestionResult(chunks, embeddings, extraction.Sheets);
    }

    // Never throws and ignores the caller's (usually cancelled) token: a cleanup failure must not replace
    // the error the processor classifies the job by.
    private async Task TryCleanUpAsync(JobTargetKinds kind, Guid documentId, string blobPath)
    {
        try
        {
            // Unconditional: SaveChangesAsync can be cancelled after the insert commits, so a
            // "did we persist" flag could read false while rows exist.
            await DeactivateDocumentRowsAsync(kind, documentId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // After a cancelled save the connection may be unusable; that must not surface to the caller.
            LogDeactivateRowsFailed(_logger, ex, documentId);

            // The blob stays: an orphaned blob is reclaimable, but an active row over deleted bytes lists,
            // matches retrieval and 404s on download.
            return;
        }

        // With the rows gone, no sweep keyed on a live document would find these bytes.
        await TryDeleteBlobAsync(blobPath, documentId);
    }

    // Authorization is the caller's: of the delete, cancel and cleanup paths, only the delete has one.
    private Task DeactivateDocumentRowsAsync(JobTargetKinds kind, Guid documentId, CancellationToken cancellationToken) =>
        kind == JobTargetKinds.Project
            ? DeactivateProjectDocumentRowsAsync(documentId, cancellationToken)
            : DeactivateConversationDocumentRowsAsync(documentId, cancellationToken);

    private async Task DeactivateConversationDocumentRowsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var date = _timeProvider.GetUtcNow();

        // One transaction, so retrieval never matches chunks under a deleted document. Set-based, so a
        // moved rowversion is a no-op rather than a DbUpdateConcurrencyException.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.ConversationDocumentSummaries
            .Where(s => s.ConversationDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheetRows
            .Where(r => r.ConversationDocumentSheet.ConversationDocumentId == documentId)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheetColumns
            .Where(c => c.ConversationDocumentSheet.ConversationDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentSheets
            .Where(s => s.ConversationDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocumentChunks
            .Where(c => c.ConversationDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ConversationDocuments
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeactivateProjectDocumentRowsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var date = _timeProvider.GetUtcNow();

        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.ProjectDocumentSummaries
            .Where(s => s.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheetRows
            .Where(r => r.ProjectDocumentSheet.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheetColumns
            .Where(c => c.ProjectDocumentSheet.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheets
            .Where(s => s.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentChunks
            .Where(c => c.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocuments
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task StoreAsync(string jobId, FileDto file, string blobPath, Dictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        _jobStatusStore.Report(jobId, JobStatuses.Uploading, UploadStart, "Storing the file");

        await _documentStore.UploadAsync(DocumentsContainer, blobPath, file.Content, metadata, cancellationToken);

        _jobStatusStore.Report(jobId, JobStatuses.Uploading, ExtractStart, "Stored the file");
    }

    private static Dictionary<string, string> BuildBlobMetadata(FileDto file, Guid userId, Guid documentId, string ownerKey, Guid ownerId)
    {
        return new Dictionary<string, string>
        {
            { "userId", userId.ToString() },
            { ownerKey, ownerId.ToString() },
            { "documentId", documentId.ToString() },
            { "contentType", file.ContentType },
            { "length", file.Length.ToString(CultureInfo.InvariantCulture) }
        };
    }

    // Matched by position; EmbedAsync rejects a short or padded generator response.
    private static List<TChunk> BuildChunks<TChunk>(IngestionResult ingestion)
        where TChunk : BaseDocumentChunk<Guid>, new()
    {
        return [.. ingestion.Chunks.Select((chunk, i) => new TChunk
        {
            Index = chunk.Index,
            SourceNumber = chunk.SourceNumber,
            TokenCount = chunk.TokenCount,
            Text = chunk.Text,
            Embedding = new SqlVector<float>(ingestion.Embeddings[i])
        })];
    }

    // The selectors exist because each document family declares its child collections on its own sheet
    // type; no shared base exposes them.
    private static List<TSheet> BuildSheets<TSheet, TColumn, TRow>(
        IngestionResult ingestion,
        Func<TSheet, List<TColumn>> selectColumns,
        Func<TSheet, List<TRow>> selectRows)
        where TSheet : BaseDocumentSheet<Guid>, new()
        where TColumn : BaseDocumentSheetColumn<Guid>, new()
        where TRow : BaseDocumentSheetRow<Guid>, new()
    {
        var sheets = new List<TSheet>(ingestion.Sheets.Count);

        foreach (var structure in ingestion.Sheets)
        {
            var sheet = new TSheet
            {
                SheetIndex = structure.SheetIndex,
                SheetName = structure.SheetName,
                RowCount = structure.RowCount,
                ColumnCount = structure.ColumnCount
            };

            selectColumns(sheet).AddRange(structure.Columns.Select(column => new TColumn
            {
                ColumnIndex = column.ColumnIndex,
                ColumnName = column.ColumnName,
                InferredType = column.InferredType
            }));

            selectRows(sheet).AddRange(structure.Rows.Select(row => new TRow
            {
                RowIndex = row.RowIndex,
                Cells = row.Cells
            }));

            sheets.Add(sheet);
        }

        return sheets;
    }

    // Best-effort, without the (usually cancelled) token: a storage error must not replace the real one.
    private async Task TryDeleteBlobAsync(string blobPath, Guid documentId)
    {
        try
        {
            await _documentStore.DeleteAsync(DocumentsContainer, blobPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogOrphanedBlob(_logger, ex, documentId);
        }
    }

    private async Task<SheetExtractionResult> ExtractAsync(string jobId, FileDto file, CancellationToken cancellationToken)
    {
        _jobStatusStore.Report(jobId, JobStatuses.Extracting, ExtractStart, "Reading the document");

        var extractor = _extractorFactory.Resolve(file.FileExtension);

        var progress = new InlineProgress<DocumentExtractionProgress>(report =>
        {
            // A null fraction means the reader cannot measure itself (remote OCR); report the band's floor.
            var percent = report.Fraction is { } fraction
                ? ExtractStart + (int)((ChunkStart - ExtractStart) * Math.Clamp(fraction, 0d, 1d))
                : ExtractStart;

            _jobStatusStore.Report(jobId, JobStatuses.Extracting, percent, report.Message);
        });

        // One call: a spreadsheet's text and grid come out of the same parse.
        var extraction = extractor is ISheetStructureExtractor sheetExtractor
            ? await ExtractSheetsAsync(sheetExtractor, file, progress, cancellationToken)
            : new SheetExtractionResult(await extractor.ExtractAsync(file, progress, cancellationToken), []);

        if (extraction.Segments.Count == 0)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "No readable text was found in the document, so there is nothing to index.")
            ]);
        }

        return extraction;
    }

    // Measured here, not in the extractors, so both formats report through one site and a ceiling
    // refusal counts as an outcome.
    private static async Task<SheetExtractionResult> ExtractSheetsAsync(
        ISheetStructureExtractor extractor,
        FileDto file,
        IProgress<DocumentExtractionProgress> progress,
        CancellationToken cancellationToken)
    {
        var documentType = file.FileExtension.ToString().ToLowerInvariant();
        var started = Stopwatch.GetTimestamp();

        try
        {
            var extraction = await extractor.ExtractSheetsAsync(file, progress, cancellationToken);

            ChatMetrics.RecordSheetIngestion(
                documentType,
                outcome: "success",
                extraction.Sheets.Count,
                extraction.Sheets.Sum(sheet => sheet.RowCount),
                extraction.Sheets.Count == 0 ? 0 : extraction.Sheets.Max(sheet => sheet.ColumnCount),
                Stopwatch.GetElapsedTime(started));

            return extraction;
        }
        catch (Exception ex)
        {
            ChatMetrics.RecordSheetIngestion(
                documentType,
                outcome: ex switch
                {
                    ValidationException => "refused",
                    OperationCanceledException when cancellationToken.IsCancellationRequested => "cancelled",
                    _ => "error"
                },
                sheetCount: 0,
                rowCount: 0,
                columnCount: 0,
                Stopwatch.GetElapsedTime(started));

            throw;
        }
    }

    private IReadOnlyList<TextChunkDto> ChunkText(string jobId, IReadOnlyList<DocumentSegmentDto> segments, CancellationToken cancellationToken)
    {
        _jobStatusStore.Report(jobId, JobStatuses.Chunking, ChunkStart, "Splitting the text for indexing");

        var chunks = _textChunker.Chunk(segments, cancellationToken);

        if (chunks.Count == 0)
        {
            throw new ValidationException(
            [
                new ValidationFailure(nameof(FileDto.FileName),
                    "No readable text was found in the document, so there is nothing to index.")
            ]);
        }

        _jobStatusStore.Report(jobId, JobStatuses.Chunking, EmbedStart, $"Split into {chunks.Count} chunk(s)", 0, chunks.Count);

        return chunks;
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(string jobId, IReadOnlyList<TextChunkDto> chunks, CancellationToken cancellationToken)
    {
        _jobStatusStore.Report(jobId, JobStatuses.Embedding, EmbedStart, $"Generating embeddings for {chunks.Count} chunk(s)", 0, chunks.Count);

        var batchSize = _options.EmbeddingBatchSize;
        var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);

        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = chunks.Skip(offset).Take(batchSize).Select(chunk => chunk.Text).ToList();
            var generated = await _embeddingGenerator.GenerateAsync(batch, cancellationToken: cancellationToken);

            if (generated.Count != batch.Count)
            {
                // Vectors match chunks by position, so a short or padded response would misattach every
                // later embedding.
                throw new InvalidOperationException(
                    $"The embedding generator returned {generated.Count} vector(s) for {batch.Count} input(s).");
            }

            vectors.AddRange(generated.Select(embedding => embedding.Vector));

            var completed = vectors.Count;
            var percent = EmbedStart + (int)((long)(PersistStart - EmbedStart) * completed / chunks.Count);
            _jobStatusStore.Report(jobId, JobStatuses.Embedding, percent, $"Embedded {completed} of {chunks.Count} chunk(s)", completed, chunks.Count);
        }

        return vectors;
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload rejected for conversation {ConversationId}: not found or not owned by user {UserId}")]
    private static partial void LogConversationUploadRejected(ILogger logger, Guid conversationId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued document upload job {JobId} for conversation {ConversationId} by user {UserId}")]
    private static partial void LogConversationUploadQueued(
        ILogger logger, string jobId, Guid conversationId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload rejected for project {ProjectId}: not found or not owned by user {UserId}")]
    private static partial void LogProjectUploadRejected(ILogger logger, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued document upload job {JobId} for project {ProjectId} by user {UserId}")]
    private static partial void LogProjectUploadQueued(ILogger logger, string jobId, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting document ingestion. Job {JobId}, conversation {ConversationId}, extension {Extension}, size {Size} bytes")]
    private static partial void LogConversationIngestionStarted(
        ILogger logger, string jobId, Guid conversationId, FileExtensions extension, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} was deactivated while job {JobId} ingested document {DocumentId}; the document was deactivated to match")]
    private static partial void LogConversationDeactivatedDuringIngestion(
        ILogger logger, Guid conversationId, string jobId, Guid documentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)")]
    private static partial void LogIngestionCompleted(ILogger logger, string jobId, Guid documentId, int chunkCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting document ingestion. Job {JobId}, project {ProjectId}, extension {Extension}, size {Size} bytes")]
    private static partial void LogProjectIngestionStarted(
        ILogger logger, string jobId, Guid projectId, FileExtensions extension, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId} was deactivated while job {JobId} ingested document {DocumentId}; the document was deactivated to match")]
    private static partial void LogProjectDeactivatedDuringIngestion(
        ILogger logger, Guid projectId, string jobId, Guid documentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delete rejected for conversation {ConversationId}: not found or not owned by user {UserId}")]
    private static partial void LogDeleteRejected(ILogger logger, Guid conversationId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} not found in conversation {ConversationId} for user {UserId}")]
    private static partial void LogDocumentNotFoundInConversation(
        ILogger logger, Guid documentId, Guid conversationId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation document {DocumentId} deactivated by user {UserId}")]
    private static partial void LogConversationDocumentDeactivated(ILogger logger, Guid documentId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cancel rejected for job {JobId}: not found or not owned by user {UserId}")]
    private static partial void LogCancelRejected(ILogger logger, string jobId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload job {JobId} cancelled by user {UserId}")]
    private static partial void LogUploadJobCancelled(ILogger logger, string jobId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download rejected for document {DocumentId}: not found or not owned by user {UserId}")]
    private static partial void LogDownloadRejected(ILogger logger, Guid documentId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Issued a download link for document {DocumentId} to user {UserId}, expiring at {ExpiresAt}")]
    private static partial void LogDownloadLinkIssued(
        ILogger logger, Guid documentId, Guid userId, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to deactivate the rows for document {DocumentId} after ingestion did not finish")]
    private static partial void LogDeactivateRowsFailed(ILogger logger, Exception exception, Guid documentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to remove the stored blob for document {DocumentId} after ingestion failed; it is now orphaned")]
    private static partial void LogOrphanedBlob(ILogger logger, Exception exception, Guid documentId);

    #endregion
}
