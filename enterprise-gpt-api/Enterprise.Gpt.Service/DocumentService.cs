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
    }

    /// <summary>
    /// Orchestrates document ingestion. Format-specific reading lives in
    /// <see cref="IDocumentTextExtractor"/> implementations and splitting in <see cref="ITextChunker"/>;
    /// this type owns sequencing, persistence and progress reporting.
    /// </summary>
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

            var jobId = Guid.NewGuid().ToString();
            _jobStatusStore.Register(jobId, userId);

            try
            {
                await _backgroundJobQueue.EnqueueAsync(new JobWorkItem(
                    jobId,
                    async (serviceProvider, token) =>
                    {
                        var documentService = serviceProvider.GetRequiredService<IDocumentService>();
                        await documentService.CreateConversationDocumentAsync(jobId, file, userId, conversationId, token);
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

            _logger.LogInformation("Queued document upload job {JobId} for conversation {ConversationId} by user {UserId}", jobId, conversationId, userId);

            return new JobDto { Id = jobId };
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

            var blobPath = await StoreAsync(jobId, file, userId, conversationId, documentId, extension, cancellationToken);

            IReadOnlyList<TextChunkDto> chunks;
            IReadOnlyList<ReadOnlyMemory<float>> embeddings;
            try
            {
                var segments = await ExtractAsync(jobId, file, cancellationToken);
                chunks = ChunkText(jobId, segments, cancellationToken);
                embeddings = await EmbedAsync(jobId, chunks, cancellationToken);
            }
            catch
            {
                // The blob is written first so the original is retained even if processing is slow, but a
                // failure after this point leaves it referenced by no database row — unreachable content
                // that would accrue storage cost forever. Best-effort cleanup, then rethrow.
                await TryDeleteBlobAsync(blobPath, documentId);
                throw;
            }

            _jobStatusStore.Report(jobId, JobStatus.Persisting, PersistStart, $"Saving {chunks.Count} chunk(s)");

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
                Chunks = [.. chunks.Select((chunk, i) => new ConversationDocumentChunk
                {
                    Index = chunk.Index,
                    SourceNumber = chunk.SourceNumber,
                    TokenCount = chunk.TokenCount,
                    Text = chunk.Text,
                    Embedding = new SqlVector<float>(embeddings[i]),
                    DateCreated = date,
                    DateModified = date
                })]
            };

            _ctx.Add(document);
            await _ctx.SaveChangesAsync(cancellationToken);

            _jobStatusStore.Complete(jobId, document.Id, $"Ready — {chunks.Count} chunk(s) indexed");
            _logger.LogInformation("Completed document ingestion. Job {JobId}, document {DocumentId}, {ChunkCount} chunk(s)", jobId, document.Id, chunks.Count);

            return document.MapToChatDocumentDto();
        }

        #region Pipeline stages
        private async Task<string> StoreAsync(string jobId, FileDto file, Guid userId, Guid conversationId, Guid documentId, string extension, CancellationToken cancellationToken)
        {
            _jobStatusStore.Report(jobId, JobStatus.Uploading, UploadStart, "Storing the file");

            var container = _configuration.GetValue<string>("AzureStorage:DocumentsContainer")!;

            // Keyed by document id rather than file name: two uploads of the same name into the same
            // conversation are legitimate, and a name-based path made the second one collide with the first
            // and fail the whole job.
            var blobPath = $"{userId}/{conversationId}/{documentId}{extension}";

            var metadata = new Dictionary<string, string>
            {
                { "userId", userId.ToString() },
                { "conversationId", conversationId.ToString() },
                { "documentId", documentId.ToString() },
                { "contentType", file.ContentType },
                { "length", file.Length.ToString() }
            };

            await _blobStorageService.UploadAsync(container, blobPath, file.Content, metadata, cancellationToken);

            _jobStatusStore.Report(jobId, JobStatus.Uploading, ExtractStart, "Stored the file");

            return blobPath;
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
