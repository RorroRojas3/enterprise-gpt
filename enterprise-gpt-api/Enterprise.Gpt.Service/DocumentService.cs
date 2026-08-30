using Azure.Storage.Sas;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlTypes;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Filtering;
using Enterprise.Gpt.Service.Mappers;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Settings;

namespace Enterprise.Gpt.Service
{
    public interface IDocumentService
    {
        /// <summary>
        /// Validates an uploaded file and queues it for ingestion into a conversation.
        /// </summary>
        /// <param name="conversationId">The conversation to attach the document to.</param>
        /// <param name="file">The uploaded file.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The identifier of the queued job, for polling upload status.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="ValidationException">Thrown when the file is empty, too large, or an unsupported format.</exception>
        /// <exception cref="NotFoundException">Thrown when the conversation does not exist or belongs to another user.</exception>
        Task<JobDto> QueueConversationDocumentAsync(Guid conversationId, FileDto file, CancellationToken cancellationToken);

        /// <summary>
        /// Runs the ingestion pipeline for a queued upload: store, extract, chunk, embed, persist.
        /// </summary>
        /// <remarks>
        /// Invoked by <see cref="BackgroundJobProcessor"/> on a background scope, never from a request
        /// thread, so <paramref name="userId"/> is passed in rather than read from the current principal.
        /// </remarks>
        /// <param name="jobId">The job identifier used to report progress.</param>
        /// <param name="file">The uploaded file.</param>
        /// <param name="userId">The owner of the document.</param>
        /// <param name="conversationId">The conversation the document belongs to.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The persisted document.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="jobId"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="ValidationException">Thrown when the document contains no readable text.</exception>
        Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto file, Guid userId, Guid conversationId, CancellationToken cancellationToken);

        /// <summary>
        /// Validates an uploaded file and queues it for ingestion into a project.
        /// </summary>
        /// <remarks>
        /// A project document is shared by every conversation in the project, which is what separates
        /// it from a conversation document: uploading into a conversation never adds to the project.
        /// </remarks>
        /// <param name="projectId">The project to attach the document to.</param>
        /// <param name="file">The uploaded file.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The identifier of the queued job, for polling upload status.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="ValidationException">Thrown when the file is empty, too large, or an unsupported format.</exception>
        /// <exception cref="NotFoundException">Thrown when the project does not exist or belongs to another user.</exception>
        Task<JobDto> QueueProjectDocumentAsync(Guid projectId, FileDto file, CancellationToken cancellationToken);

        /// <summary>
        /// Runs the ingestion pipeline for a queued project upload: store, extract, chunk, embed, persist.
        /// </summary>
        /// <remarks>
        /// Invoked by <see cref="BackgroundJobProcessor"/> on a background scope, never from a request
        /// thread, so <paramref name="userId"/> is passed in rather than read from the current principal.
        /// </remarks>
        /// <param name="jobId">The job identifier used to report progress.</param>
        /// <param name="file">The uploaded file.</param>
        /// <param name="userId">The owner of the document.</param>
        /// <param name="projectId">The project the document belongs to.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The persisted document.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="jobId"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="ValidationException">Thrown when the document contains no readable text.</exception>
        Task<ProjectDocumentDto> CreateProjectDocumentAsync(string jobId, FileDto file, Guid userId, Guid projectId, CancellationToken cancellationToken);

        /// <summary>
        /// Issues a short-lived link to the original file behind a conversation document.
        /// </summary>
        /// <remarks>
        /// The bytes are never proxied through this process: the caller is authorized here, and storage
        /// serves the download itself. See <see cref="DocumentDownloadDto"/> for what that means for the
        /// link's confidentiality.
        /// </remarks>
        /// <param name="conversationId">The conversation the document belongs to.</param>
        /// <param name="documentId">The document to download.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The signed link and the file name to save it under.</returns>
        /// <exception cref="NotFoundException">
        /// Thrown when the document does not exist, is deactivated, or belongs to a conversation the
        /// caller does not own.
        /// </exception>
        /// <exception cref="StorageNotConfiguredException">Thrown when storage cannot sign a link.</exception>
        Task<DocumentDownloadDto> GetConversationDocumentDownloadAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken);

        /// <summary>
        /// Issues a short-lived link to the original file behind a project document.
        /// </summary>
        /// <remarks>
        /// The bytes are never proxied through this process: the caller is authorized here, and storage
        /// serves the download itself. See <see cref="DocumentDownloadDto"/> for what that means for the
        /// link's confidentiality.
        /// </remarks>
        /// <param name="projectId">The project the document belongs to.</param>
        /// <param name="documentId">The document to download.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The signed link and the file name to save it under.</returns>
        /// <exception cref="NotFoundException">
        /// Thrown when the document does not exist, is deactivated, or belongs to a project the caller
        /// does not own.
        /// </exception>
        /// <exception cref="StorageNotConfiguredException">Thrown when storage cannot sign a link.</exception>
        Task<DocumentDownloadDto> GetProjectDocumentDownloadAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken);

        /// <summary>
        /// Lists the caller's active conversation documents across every conversation, newest first,
        /// as one page for the documents library.
        /// </summary>
        /// <remarks>
        /// Paging arguments are clamped rather than rejected — <paramref name="skip"/> to zero or
        /// above and <paramref name="take"/> to 1..100 — while an unrecognised
        /// <paramref name="type"/> is rejected. A blank <paramref name="name"/> narrows nothing.
        /// </remarks>
        /// <param name="skip">The number of documents to skip.</param>
        /// <param name="take">The maximum number of documents to return.</param>
        /// <param name="type">The origin to narrow to; <see langword="null"/> or blank for every document.</param>
        /// <param name="name">An optional case-insensitive substring of the document name to filter on.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>A page of the caller's conversation documents.</returns>
        /// <exception cref="ValidationException">Thrown when <paramref name="type"/> names no supported origin.</exception>
        Task<PaginatedResponseDto<UserDocumentDto>> GetUserDocumentsAsync(int skip = 0, int take = 20, string? type = null, string? name = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Orchestrates document ingestion. Format-specific reading lives in
    /// <see cref="IDocumentTextExtractor"/> implementations and splitting in <see cref="ITextChunker"/>;
    /// this type owns sequencing, persistence and progress reporting.
    /// </summary>
    /// <remarks>
    /// Conversation and project uploads share one pipeline: they differ only in the ownership check,
    /// the blob key, and the entity persisted at the end.
    /// </remarks>
    public class DocumentService(ILogger<DocumentService> logger,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IBlobStorageService blobStorageService,
        IConfiguration configuration,
        IOptions<DocumentOptions> options,
        IDocumentTextExtractorFactory extractorFactory,
        ITextChunker textChunker,
        IValidator<FileDto> fileValidator,
        IBackgroundJobQueue backgroundJobQueue,
        IJobStatusStore jobStatusStore,
        ITokenService tokenService,
        EnterpriseGptDbContext ctx) : IDocumentService
    {
        #region Progress bands
        // Each stage owns a slice of the 0..100 range, sized roughly by how long it takes. Embedding gets
        // the widest band because it is the longest phase and the only one whose progress is countable, so
        // that is where a moving percentage is worth the most.
        private const int UploadStart = 0;
        private const int ExtractStart = 10;
        private const int ChunkStart = 45;
        private const int EmbedStart = 50;
        private const int PersistStart = 92;
        #endregion

        private readonly ILogger<DocumentService> _logger = logger;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
        private readonly IBlobStorageService _blobStorageService = blobStorageService;
        private readonly IConfiguration _configuration = configuration;
        private readonly DocumentOptions _options = options.Value;
        private readonly IDocumentTextExtractorFactory _extractorFactory = extractorFactory;
        private readonly ITextChunker _textChunker = textChunker;
        private readonly IValidator<FileDto> _fileValidator = fileValidator;
        private readonly IBackgroundJobQueue _backgroundJobQueue = backgroundJobQueue;
        private readonly IJobStatusStore _jobStatusStore = jobStatusStore;
        private readonly ITokenService _tokenService = tokenService;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        /// <summary>The container every uploaded document blob lives in.</summary>
        /// <exception cref="StorageNotConfiguredException">Thrown when the container name is not configured.</exception>
        private string DocumentsContainer =>
            DocumentStorage.RequireContainer(_configuration, _logger, DocumentStorage.DocumentsContainerKey);

        /// <summary>The container every generated document blob lives in.</summary>
        /// <remarks>
        /// A container of its own rather than a prefix inside the uploads one, so a generated file and an
        /// uploaded file never share a retention policy, an access policy or a lifecycle rule.
        /// </remarks>
        /// <exception cref="StorageNotConfiguredException">Thrown when the container name is not configured.</exception>
        private string GeneratedContainer =>
            DocumentStorage.RequireContainer(_configuration, _logger, DocumentStorage.GeneratedContainerKey);

        /// <inheritdoc />
        public async Task<JobDto> QueueConversationDocumentAsync(Guid conversationId, FileDto file, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);

            await _fileValidator.ValidateAndThrowAsync(file, cancellationToken);

            // Resolved here, on the request thread: the background scope has no HttpContext to read it from.
            var userId = _tokenService.GetOid();

            var conversationExists = await _ctx.Conversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == conversationId && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!conversationExists)
            {
                // Deliberately indistinguishable from "does not exist" so the endpoint cannot be used to
                // probe for conversation ids belonging to other users.
                _logger.LogWarning("Upload rejected for conversation {ConversationId}: not found or not owned by user {UserId}", conversationId, userId);
                throw new NotFoundException($"Conversation with id '{conversationId}' was not found.");
            }

            var job = await EnqueueAsync(
                userId,
                (documentService, jobId, token) => documentService.CreateConversationDocumentAsync(jobId, file, userId, conversationId, token),
                cancellationToken);

            _logger.LogInformation("Queued document upload job {JobId} for conversation {ConversationId} by user {UserId}", job.Id, conversationId, userId);

            return job;
        }

        /// <inheritdoc />
        public async Task<JobDto> QueueProjectDocumentAsync(Guid projectId, FileDto file, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(file);

            await _fileValidator.ValidateAndThrowAsync(file, cancellationToken);

            var userId = _tokenService.GetOid();

            var projectExists = await _ctx.Projects
                .AsNoTracking()
                .AnyAsync(x => x.Id == projectId && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!projectExists)
            {
                _logger.LogWarning("Upload rejected for project {ProjectId}: not found or not owned by user {UserId}", projectId, userId);
                throw new NotFoundException($"Project with id '{projectId}' was not found.");
            }

            var job = await EnqueueAsync(
                userId,
                (documentService, jobId, token) => documentService.CreateProjectDocumentAsync(jobId, file, userId, projectId, token),
                cancellationToken);

            _logger.LogInformation("Queued document upload job {JobId} for project {ProjectId} by user {UserId}", job.Id, projectId, userId);

            return job;
        }

        /// <inheritdoc />
        public async Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto file, Guid userId, Guid conversationId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            ArgumentNullException.ThrowIfNull(file);

            _logger.LogInformation("Starting document ingestion. Job {JobId}, conversation {ConversationId}, extension {Extension}, size {Size} bytes",
                jobId, conversationId, file.FileExtension, file.Length);

            var documentId = Guid.NewGuid();
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            // Keyed by document id rather than file name: two uploads of the same name into the same
            // conversation are legitimate, and a name-based path made the second one collide with the first
            // and fail the whole job.
            var blobPath = $"{userId}/{conversationId}/{documentId}{extension}";

            var ingestion = await IngestAsync(jobId, file, documentId, blobPath,
                BuildBlobMetadata(file, userId, documentId, "conversationId", conversationId), cancellationToken);

            _jobStatusStore.Report(jobId, JobStatus.Persisting, PersistStart, $"Saving {ingestion.Chunks.Count} chunk(s)");

            var date = DateTimeOffset.UtcNow;
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
                DateCreated = date,
                DateModified = date,
                Chunks = BuildChunks<ConversationDocumentChunk>(ingestion, date),
                Sheets = BuildSheets<ConversationDocumentSheet, ConversationDocumentSheetColumn, ConversationDocumentSheetRow>(
                    ingestion, date, sheet => sheet.Columns, sheet => sheet.Rows)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            // Ingestion spans seconds to minutes, and a conversation deactivated meanwhile has already
            // run its document cascade, which never re-runs — so this insert would otherwise leave an
            // active document under a deactivated conversation, which the listings treat as impossible.
            // Re-checking after the save shrinks that window to the cascade's own statement gap.
            var conversationStillActive = await _ctx.Conversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == conversationId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!conversationStillActive)
            {
                // Set-based rather than through the tracked entities: if the cascade caught these rows
                // between the insert and the re-check, their rowversions have moved and a tracked save
                // would throw DbUpdateConcurrencyException; this shape is a no-op in that case.
                var deactivatedAt = DateTimeOffset.UtcNow;

                // Every table is named because nothing cascades, and leaf first because this path has
                // no transaction: a reader must never find a live cell row under a dead sheet.
                var sheetIds = _ctx.ConversationDocumentSheets
                    .Where(s => s.ConversationDocumentId == document.Id)
                    .Select(s => s.Id);

                await _ctx.ConversationDocumentSheetRows
                    .Where(r => sheetIds.Contains(r.ConversationDocumentSheetId) && !r.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(r => r
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ConversationDocumentSheetColumns
                    .Where(c => sheetIds.Contains(c.ConversationDocumentSheetId) && !c.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(c => c
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ConversationDocumentSheets
                    .Where(s => s.ConversationDocumentId == document.Id && !s.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ConversationDocumentChunks
                    .Where(c => c.ConversationDocumentId == document.Id && !c.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(c => c
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ConversationDocuments
                    .Where(d => d.Id == document.Id && !d.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                _logger.LogWarning("Conversation {ConversationId} was deactivated while job {JobId} ingested document {DocumentId}; the document was deactivated to match", conversationId, jobId, document.Id);
                throw new NotFoundException($"Conversation with id '{conversationId}' was not found.");
            }

            _jobStatusStore.Complete(jobId, document.Id, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed");
            _logger.LogInformation("Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)", jobId, document.Id, ingestion.Chunks.Count);

            return document.MapToConversationDocumentDto();
        }

        /// <inheritdoc />
        public async Task<ProjectDocumentDto> CreateProjectDocumentAsync(string jobId, FileDto file, Guid userId, Guid projectId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            ArgumentNullException.ThrowIfNull(file);

            _logger.LogInformation("Starting document ingestion. Job {JobId}, project {ProjectId}, extension {Extension}, size {Size} bytes",
                jobId, projectId, file.FileExtension, file.Length);

            var documentId = Guid.NewGuid();
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            // The literal "projects" segment is what keeps this from ever colliding with a conversation
            // key: both are "{userId}/{ownerId}/..." and the two id spaces are unrelated guids.
            var blobPath = $"{userId}/projects/{projectId}/{documentId}{extension}";

            var ingestion = await IngestAsync(jobId, file, documentId, blobPath,
                BuildBlobMetadata(file, userId, documentId, "projectId", projectId), cancellationToken);

            _jobStatusStore.Report(jobId, JobStatus.Persisting, PersistStart, $"Saving {ingestion.Chunks.Count} chunk(s)");

            var date = DateTimeOffset.UtcNow;
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
                DateCreated = date,
                DateModified = date,
                Chunks = BuildChunks<ProjectDocumentChunk>(ingestion, date),
                Sheets = BuildSheets<ProjectDocumentSheet, ProjectDocumentSheetColumn, ProjectDocumentSheetRow>(
                    ingestion, date, sheet => sheet.Columns, sheet => sheet.Rows)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            // Same mid-ingest deactivation window as the conversation path: the project's document
            // cascade never re-runs, so a row persisted after it must deactivate itself to match.
            var projectStillActive = await _ctx.Projects
                .AsNoTracking()
                .AnyAsync(x => x.Id == projectId && !x.DateDeactivated.HasValue, cancellationToken);

            if (!projectStillActive)
            {
                // Set-based for the same reason as the conversation path: idempotent against the
                // cascade having already deactivated these rows, with no concurrency token involved.
                var deactivatedAt = DateTimeOffset.UtcNow;

                var sheetIds = _ctx.ProjectDocumentSheets
                    .Where(s => s.ProjectDocumentId == document.Id)
                    .Select(s => s.Id);

                await _ctx.ProjectDocumentSheetRows
                    .Where(r => sheetIds.Contains(r.ProjectDocumentSheetId) && !r.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(r => r
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ProjectDocumentSheetColumns
                    .Where(c => sheetIds.Contains(c.ProjectDocumentSheetId) && !c.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(c => c
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ProjectDocumentSheets
                    .Where(s => s.ProjectDocumentId == document.Id && !s.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ProjectDocumentChunks
                    .Where(c => c.ProjectDocumentId == document.Id && !c.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(c => c
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                await _ctx.ProjectDocuments
                    .Where(d => d.Id == document.Id && !d.DateDeactivated.HasValue)
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.DateDeactivated, deactivatedAt),
                        cancellationToken);

                _logger.LogWarning("Project {ProjectId} was deactivated while job {JobId} ingested document {DocumentId}; the document was deactivated to match", projectId, jobId, document.Id);
                throw new NotFoundException($"Project with id '{projectId}' was not found.");
            }

            _jobStatusStore.Complete(jobId, document.Id, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed");
            _logger.LogInformation("Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)", jobId, document.Id, ingestion.Chunks.Count);

            return document.MapToProjectDocumentDto();
        }

        /// <inheritdoc />
        public async Task<DocumentDownloadDto> GetConversationDocumentDownloadAsync(Guid conversationId, Guid documentId, CancellationToken cancellationToken)
        {
            var userId = _tokenService.GetOid();

            // Ownership is read off the conversation rather than ConversationDocument.UserId, so the rule
            // that decides a document is downloadable is the same one that made it visible. One query
            // authorizes and reads the blob reference together; projecting to a record keeps the chunk
            // rows and their embeddings out of it.
            var document = await _ctx.ConversationDocuments
                .AsNoTracking()
                .Where(x => x.Id == documentId
                    && x.ConversationId == conversationId
                    && !x.DateDeactivated.HasValue
                    && x.Conversation.UserId == userId
                    && !x.Conversation.DateDeactivated.HasValue)
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
                    && !x.DateDeactivated.HasValue
                    && x.Project.UserId == userId
                    && !x.Project.DateDeactivated.HasValue)
                .Select(x => new DocumentBlobRef(x.Name, x.Extension, x.Path, false))
                .FirstOrDefaultAsync(cancellationToken);

            return BuildDownload(document, documentId, userId);
        }

        /// <inheritdoc />
        public async Task<PaginatedResponseDto<UserDocumentDto>> GetUserDocumentsAsync(int skip = 0, int take = 20, string? type = null, string? name = null, CancellationToken cancellationToken = default)
        {
            // Resolved before any database work, so a misspelled origin costs a round trip to nothing
            // rather than a page the caller then has to distrust.
            var origin = DocumentTypeNames.Resolve(type);

            // Clamped rather than validated: the paging arguments come straight off the query
            // string, and take = 0 would divide by zero when computing CurrentPage below.
            skip = Math.Max(skip, 0);
            take = Math.Clamp(take, 1, 100);

            var userId = _tokenService.GetOid();

            // No conversation-active predicate: DeactivateConversationAsync and its bulk sibling
            // deactivate the conversation's documents in the same operation, and ingestion re-checks
            // the conversation after persisting, so an active document implies an active conversation.
            // The Conversation join in the projection exists only to carry the name, and the
            // (UserId, DateDeactivated) index serves this filter.
            // Both origins: a generated row's own write path re-checks the conversation after
            // persisting, so the invariant above holds for it too.
            var query = _ctx.ConversationDocuments
                .AsNoTracking()
                .Where(x => x.UserId == userId && !x.DateDeactivated.HasValue);

            if (origin.HasValue)
            {
                query = query.Where(x => x.Type == origin.Value);
            }

            // Before the count, so TotalCount describes the filtered set rather than the whole listing.
            // Server-side rather than in the client, because a name filter over whatever page happened
            // to be loaded cannot reach a document ranked past it.
            if (!string.IsNullOrWhiteSpace(name))
            {
                query = query.Where(x => EF.Functions.Like(x.Name, $"%{name}%"));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                // Id breaks ties: DateCreated alone is not a total order, so two documents ingested
                // in the same tick could straddle a page boundary and be duplicated or skipped.
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

        #region Downloads
        /// <summary>
        /// Turns a resolved blob reference into a signed, expiring download link.
        /// </summary>
        /// <param name="document">The blob reference, or <see langword="null"/> when the query matched nothing.</param>
        /// <param name="documentId">The requested document, for the failure message and the audit line.</param>
        /// <param name="userId">The caller, for the audit line.</param>
        /// <returns>The link and the file name to save it under.</returns>
        private DocumentDownloadDto BuildDownload(DocumentBlobRef? document, Guid documentId, Guid userId)
        {
            if (document is null)
            {
                // One message covers all four ways the query can miss — no such document, deactivated,
                // wrong owner id in the route, or someone else's parent — so this route cannot be used
                // to probe for document ids. 404 rather than 403, as everywhere else here.
                _logger.LogWarning("Download rejected for document {DocumentId}: not found or not owned by user {UserId}", documentId, userId);
                throw new NotFoundException($"Document with id '{documentId}' was not found.");
            }

            var lifetime = TimeSpan.FromMinutes(_options.DownloadUrlLifetimeMinutes);

            // Recomputed rather than read back off the URI: the token's expiry is not exposed, and the
            // two are set from the same clock microseconds apart.
            var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);

            var sasUri = _blobStorageService.GenerateSasUri(
                document.IsGenerated ? GeneratedContainer : DocumentsContainer,
                document.Path,
                lifetime,
                BlobSasPermissions.Read,
                BuildContentDisposition(document.Name),
                DocumentStorage.ResolveContentType(document.Extension));

            // The link is a bearer credential until it expires, so only the document id is recorded.
            _logger.LogInformation("Issued a download link for document {DocumentId} to user {UserId}, expiring at {ExpiresAt}", documentId, userId, expiresAt);

            return new DocumentDownloadDto
            {
                DownloadUrl = sasUri.ToString(),
                FileName = document.Name,
                ExpiresAt = expiresAt
            };
        }

        /// <summary>
        /// Builds the RFC 6266 <c>Content-Disposition</c> the download link overrides the blob's with.
        /// </summary>
        /// <param name="fileName">The original file name.</param>
        /// <returns>An <c>attachment</c> disposition naming the file.</returns>
        /// <remarks>
        /// Both forms are emitted: <c>filename*</c> carries the real name so a non-ASCII one survives,
        /// and the quoted <c>filename</c> is the fallback. The fallback is the only part that is not
        /// percent-encoded, so quotes, backslashes and control characters are stripped out of it rather
        /// than allowed to terminate the header early.
        /// </remarks>
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


        /// <summary>
        /// The columns a download needs, projected server-side so no chunk or embedding data is read.
        /// </summary>
        /// <param name="IsGenerated">
        /// Whether the blob lives in the generated-documents container. Read off the row rather than
        /// inferred from the extension: both containers hold the same seven formats.
        /// </param>
        private sealed record DocumentBlobRef(string Name, string Extension, string Path, bool IsGenerated);
        #endregion

        #region Pipeline stages
        /// <summary>
        /// Registers a job and enqueues the ingestion work for it.
        /// </summary>
        /// <param name="userId">The caller, recorded on the job so only they can poll it.</param>
        /// <param name="work">The ingestion call to make on the background scope's service.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The registered job.</returns>
        private async Task<JobDto> EnqueueAsync(
            Guid userId,
            Func<IDocumentService, string, CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            var jobId = Guid.NewGuid().ToString();
            _jobStatusStore.Register(jobId, userId);

            try
            {
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
                // Register succeeded but enqueue did not — the snapshot would otherwise sit in Queued
                // forever (JobStatusStore only evicts terminal states). Mark it Failed so retention can
                // reclaim it and any racing poll sees a deterministic terminal state.
                _jobStatusStore.Fail(jobId, "The upload could not be queued for processing. Please try again.");
                throw;
            }

            return new JobDto { Id = jobId };
        }

        /// <summary>
        /// Runs the owner-agnostic half of ingestion: store the original, read it, split it, embed it.
        /// </summary>
        /// <param name="jobId">The job identifier used to report progress.</param>
        /// <param name="file">The uploaded file.</param>
        /// <param name="documentId">The identifier the caller will persist the document under.</param>
        /// <param name="blobPath">The storage key to write the original to.</param>
        /// <param name="metadata">Blob metadata describing the upload and its owner.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The chunks and their embeddings, positionally aligned.</returns>
        private async Task<IngestionResult> IngestAsync(
            string jobId, FileDto file, Guid documentId, string blobPath,
            Dictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            await StoreAsync(jobId, file, blobPath, metadata, cancellationToken);

            try
            {
                var extraction = await ExtractAsync(jobId, file, cancellationToken);
                var chunks = ChunkText(jobId, extraction.Segments, cancellationToken);
                var embeddings = await EmbedAsync(jobId, chunks, cancellationToken);

                return new IngestionResult(chunks, embeddings, extraction.Sheets);
            }
            catch
            {
                // The blob is written first so the original is retained even if processing is slow, but a
                // failure after this point leaves it referenced by no database row — unreachable content
                // that would accrue storage cost forever. Best-effort cleanup, then rethrow.
                await TryDeleteBlobAsync(blobPath, documentId);
                throw;
            }
        }

        private async Task StoreAsync(string jobId, FileDto file, string blobPath, Dictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            _jobStatusStore.Report(jobId, JobStatus.Uploading, UploadStart, "Storing the file");

            await _blobStorageService.UploadAsync(DocumentsContainer, blobPath, file.Content, metadata, cancellationToken);

            _jobStatusStore.Report(jobId, JobStatus.Uploading, ExtractStart, "Stored the file");
        }

        private static Dictionary<string, string> BuildBlobMetadata(FileDto file, Guid userId, Guid documentId, string ownerKey, Guid ownerId)
        {
            return new Dictionary<string, string>
            {
                { "userId", userId.ToString() },
                { ownerKey, ownerId.ToString() },
                { "documentId", documentId.ToString() },
                { "contentType", file.ContentType },
                { "length", file.Length.ToString() }
            };
        }

        /// <summary>
        /// Builds the persisted chunk rows for a document from an ingestion result.
        /// </summary>
        /// <remarks>
        /// Vectors are matched to chunks by position, which <see cref="EmbedAsync"/> guarantees by
        /// rejecting a short or padded response from the generator.
        /// </remarks>
        private static List<TChunk> BuildChunks<TChunk>(IngestionResult ingestion, DateTimeOffset date)
            where TChunk : BaseDocumentChunk, new()
        {
            return [.. ingestion.Chunks.Select((chunk, i) => new TChunk
            {
                Index = chunk.Index,
                SourceNumber = chunk.SourceNumber,
                TokenCount = chunk.TokenCount,
                Text = chunk.Text,
                Embedding = new SqlVector<float>(ingestion.Embeddings[i]),
                DateCreated = date,
                DateModified = date
            })];
        }

        /// <summary>
        /// Builds the persisted sheet rows, with their columns and cell rows, for a spreadsheet.
        /// </summary>
        /// <param name="selectColumns">Reaches the sheet's own column collection.</param>
        /// <param name="selectRows">Reaches the sheet's own row collection.</param>
        /// <remarks>
        /// The two selectors are what keeps this one implementation: the child collections are declared
        /// on each document family's own sheet type, so no shared base exposes them.
        /// </remarks>
        private static List<TSheet> BuildSheets<TSheet, TColumn, TRow>(
            IngestionResult ingestion,
            DateTimeOffset date,
            Func<TSheet, List<TColumn>> selectColumns,
            Func<TSheet, List<TRow>> selectRows)
            where TSheet : BaseDocumentSheet, new()
            where TColumn : BaseDocumentSheetColumn, new()
            where TRow : BaseDocumentSheetRow, new()
        {
            var sheets = new List<TSheet>(ingestion.Sheets.Count);

            foreach (var structure in ingestion.Sheets)
            {
                var sheet = new TSheet
                {
                    SheetIndex = structure.SheetIndex,
                    SheetName = structure.SheetName,
                    RowCount = structure.RowCount,
                    ColumnCount = structure.ColumnCount,
                    DateCreated = date,
                    DateModified = date
                };

                selectColumns(sheet).AddRange(structure.Columns.Select(column => new TColumn
                {
                    ColumnIndex = column.ColumnIndex,
                    ColumnName = column.ColumnName,
                    InferredType = column.InferredType,
                    DateCreated = date,
                    DateModified = date
                }));

                selectRows(sheet).AddRange(structure.Rows.Select(row => new TRow
                {
                    RowIndex = row.RowIndex,
                    Cells = row.Cells,
                    DateCreated = date,
                    DateModified = date
                }));

                sheets.Add(sheet);
            }

            return sheets;
        }

        /// <summary>
        /// Removes a stored blob whose document was never persisted, so a failed ingestion leaves nothing
        /// behind.
        /// </summary>
        /// <remarks>
        /// Best-effort and never throws: the caller is already unwinding a failure, and losing the cleanup
        /// must not replace the real error with a storage one. A leaked blob is logged for later reaping.
        /// The cancellation token is deliberately not passed on — the common reason to be here is that the
        /// token was cancelled, and cleanup still needs to run.
        /// </remarks>
        private async Task TryDeleteBlobAsync(string blobPath, Guid documentId)
        {
            try
            {
                await _blobStorageService.DeleteAsync(DocumentsContainer, blobPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove the stored blob for document {DocumentId} after ingestion failed; it is now orphaned", documentId);
            }
        }

        /// <summary>
        /// Reads the file into segments, and into the sheets behind them where the format has any.
        /// </summary>
        private async Task<SheetExtractionResult> ExtractAsync(string jobId, FileDto file, CancellationToken cancellationToken)
        {
            _jobStatusStore.Report(jobId, JobStatus.Extracting, ExtractStart, "Reading the document");

            var extractor = _extractorFactory.Resolve(file.FileExtension);

            var progress = new InlineProgress<DocumentExtractionProgress>(report =>
            {
                // A null fraction means the reader cannot measure itself (a remote OCR call is opaque until
                // it returns). Reporting the band's floor keeps the percentage honest while the refreshed
                // message and timestamp still show the job is alive.
                var percent = report.Fraction is { } fraction
                    ? ExtractStart + (int)((ChunkStart - ExtractStart) * Math.Clamp(fraction, 0d, 1d))
                    : ExtractStart;

                _jobStatusStore.Report(jobId, JobStatus.Extracting, percent, report.Message);
            });

            // One call, not two: a spreadsheet's text and its grid come out of the same parse, and
            // asking for them separately would open the package twice for nothing.
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

        /// <summary>
        /// Reads a spreadsheet's text and grid, recording the shape and cost of the read.
        /// </summary>
        /// <remarks>
        /// Measured here rather than inside the extractors, so both formats report through one site and a
        /// ceiling refusal is counted as the outcome it is rather than as a read that never happened.
        /// </remarks>
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
            _jobStatusStore.Report(jobId, JobStatus.Chunking, ChunkStart, "Splitting the text for indexing");

            var chunks = _textChunker.Chunk(segments, cancellationToken);

            if (chunks.Count == 0)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(FileDto.FileName),
                        "No readable text was found in the document, so there is nothing to index.")
                ]);
            }

            _jobStatusStore.Report(jobId, JobStatus.Chunking, EmbedStart, $"Split into {chunks.Count} chunk(s)", 0, chunks.Count);

            return chunks;
        }

        /// <summary>
        /// Generates one embedding per chunk, in batches, reporting progress after each batch.
        /// </summary>
        /// <remarks>
        /// Uses the generator's batch overload rather than issuing one request per chunk: a single request
        /// carrying 64 inputs replaces 64 round-trips, and each completed batch is a natural, truthful
        /// progress tick.
        /// </remarks>
        private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(string jobId, IReadOnlyList<TextChunkDto> chunks, CancellationToken cancellationToken)
        {
            _jobStatusStore.Report(jobId, JobStatus.Embedding, EmbedStart, $"Generating embeddings for {chunks.Count} chunk(s)", 0, chunks.Count);

            var batchSize = _options.EmbeddingBatchSize;
            var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);

            for (var offset = 0; offset < chunks.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = chunks.Skip(offset).Take(batchSize).Select(chunk => chunk.Text).ToList();
                var generated = await _embeddingGenerator.GenerateAsync(batch, cancellationToken: cancellationToken);

                if (generated.Count != batch.Count)
                {
                    // Vectors are matched to chunks by position, so a short or padded response would
                    // silently attach the wrong embedding to every chunk after the gap.
                    throw new InvalidOperationException(
                        $"The embedding generator returned {generated.Count} vector(s) for {batch.Count} input(s).");
                }

                vectors.AddRange(generated.Select(embedding => embedding.Vector));

                var completed = vectors.Count;
                var percent = EmbedStart + (int)((long)(PersistStart - EmbedStart) * completed / chunks.Count);
                _jobStatusStore.Report(jobId, JobStatus.Embedding, percent, $"Embedded {completed} of {chunks.Count} chunk(s)", completed, chunks.Count);
            }

            return vectors;
        }
        #endregion

        /// <summary>
        /// The output of the owner-agnostic pipeline: chunks and their positionally aligned vectors,
        /// plus the structured sheets a spreadsheet also yielded.
        /// </summary>
        private sealed record IngestionResult(
            IReadOnlyList<TextChunkDto> Chunks,
            IReadOnlyList<ReadOnlyMemory<float>> Embeddings,
            IReadOnlyList<SheetStructureDto> Sheets);

        /// <summary>
        /// An <see cref="IProgress{T}"/> that runs its callback on the calling thread.
        /// </summary>
        /// <remarks>
        /// <see cref="Progress{T}"/> posts each report to the thread pool, which lets reports arrive out of
        /// order and land after the stage that raised them has finished — so a stale message could overwrite
        /// a newer one. Reporting inline keeps the status feed ordered; the callback only touches a
        /// concurrent dictionary, so there is nothing to offload.
        /// </remarks>
        private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
        {
            private readonly Action<T> _handler = handler;

            public void Report(T value) => _handler(value);
        }
    }
}
