using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlTypes;
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
using Enterprise.Gpt.Service.Mappers;
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
                Chunks = BuildChunks<ConversationDocumentChunk>(ingestion, date)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            _jobStatusStore.Complete(jobId, document.Id, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed");
            _logger.LogInformation("Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)", jobId, document.Id, ingestion.Chunks.Count);

            return document.MapToChatDocumentDto();
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
                Chunks = BuildChunks<ProjectDocumentChunk>(ingestion, date)
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            _jobStatusStore.Complete(jobId, document.Id, $"Ready — {ingestion.Chunks.Count} chunk(s) indexed");
            _logger.LogInformation("Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)", jobId, document.Id, ingestion.Chunks.Count);

            return document.MapToProjectDocumentDto();
        }

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
                var segments = await ExtractAsync(jobId, file, cancellationToken);
                var chunks = ChunkText(jobId, segments, cancellationToken);
                var embeddings = await EmbedAsync(jobId, chunks, cancellationToken);

                return new IngestionResult(chunks, embeddings);
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

            var container = _configuration.GetValue<string>("AzureStorage:DocumentsContainer")!;

            await _blobStorageService.UploadAsync(container, blobPath, file.Content, metadata, cancellationToken);

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
                var container = _configuration.GetValue<string>("AzureStorage:DocumentsContainer")!;
                await _blobStorageService.DeleteAsync(container, blobPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove the stored blob for document {DocumentId} after ingestion failed; it is now orphaned", documentId);
            }
        }

        private async Task<IReadOnlyList<DocumentSegmentDto>> ExtractAsync(string jobId, FileDto file, CancellationToken cancellationToken)
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

            var segments = await extractor.ExtractAsync(file, progress, cancellationToken);

            if (segments.Count == 0)
            {
                throw new ValidationException(
                [
                    new ValidationFailure(nameof(FileDto.FileName),
                        "No readable text was found in the document, so there is nothing to index.")
                ]);
            }

            return segments;
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
        /// The output of the owner-agnostic pipeline: chunks and their positionally aligned vectors.
        /// </summary>
        private sealed record IngestionResult(
            IReadOnlyList<TextChunkDto> Chunks,
            IReadOnlyList<ReadOnlyMemory<float>> Embeddings);

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
