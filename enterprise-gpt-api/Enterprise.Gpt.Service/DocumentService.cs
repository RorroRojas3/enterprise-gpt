using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.BackgroundJobs;

namespace Enterprise.Gpt.Service
{
    public interface IDocumentService
    {
        Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto fileDataDto, Guid userId, Guid chatId, CancellationToken cancellationToken);

        Task<List<DocumentExtractorDto>> ExtractTextAsync(FileDto fileDto, CancellationToken cancellationToken);

        Task<PageEmbeddingDto> GeneratePageEmbeddingAsync(DocumentExtractorDto documentExtractor, CancellationToken cancellationToken);
    }

    public class DocumentService(ILogger<DocumentService> logger,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IBlobStorageService blobStorageService,
        IConfiguration configuration,
        IDocumentIntelligenceService documentIntelligenceService,
        IJobStatusStore jobStatusStore,
        EnterpriseGptDbContext ctx) : IDocumentService
    {
        private readonly ILogger _logger = logger;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
        private readonly IBlobStorageService _blobStorageService = blobStorageService;
        private readonly IConfiguration _configuration = configuration;
        private readonly IDocumentIntelligenceService _documentIntelligenceService = documentIntelligenceService;
        private readonly IJobStatusStore _jobStatusStore = jobStatusStore;
        private readonly EnterpriseGptDbContext _ctx = ctx;

        public async Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto fileDataDto, Guid userId, Guid chatId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobId, nameof(jobId));
            ArgumentNullException.ThrowIfNull(fileDataDto, nameof(fileDataDto));

            _logger.LogInformation("Starting document creation. Job ID: {JobId}, Chat ID: {ChatId}, File Name: {FileName}", jobId, chatId, fileDataDto.FileName);

            _jobStatusStore.Update(jobId, JobStatus.Uploading, 25);

            string container = _configuration.GetValue<string>("AzureStorage:DocumentsContainer")!;
            string blob = $"{userId}/{chatId}/{fileDataDto.FileName}";
            Dictionary<string, string> metadata = new()
            {
                { "userId", userId.ToString() },
                { "conversationId", chatId.ToString() },
                { "fileName", fileDataDto.FileName },
                { "contentType", fileDataDto.ContentType },
                { "length", fileDataDto.Length.ToString() }
            };

            await _blobStorageService.UploadAsync(container, blob, fileDataDto.Content, metadata, cancellationToken);

            _jobStatusStore.Update(jobId, JobStatus.Extracting, 50);
            var documentExtractors = await ExtractTextAsync(fileDataDto, cancellationToken);

            List<ConversationDocumentPage> documentPages = [];
            var date = DateTimeOffset.UtcNow;

            var tasks = new List<Task<PageEmbeddingDto>>();

            foreach (var documentExtractor in documentExtractors)
            {
                var task = GeneratePageEmbeddingAsync(documentExtractor, cancellationToken);
                tasks.Add(task);

                // Process in batches of 10
                if (tasks.Count == 10)
                {
                    var completedTasks = await Task.WhenAll(tasks);
                    foreach (var result in completedTasks)
                    {
                        documentPages.Add(new ConversationDocumentPage
                        {
                            Number = result.Number,
                            Embedding = new SqlVector<float>(result.Embedding),
                            Text = result.Text,
                            DateCreated = date,
                            DateModified = date
                        });
                    }
                    tasks.Clear();
                }
            }

            // Process remaining tasks (less than 10)
            if (tasks.Count > 0)
            {
                var completedTasks = await Task.WhenAll(tasks);
                foreach (var result in completedTasks)
                {
                    documentPages.Add(new ConversationDocumentPage
                    {
                        Number = result.Number,
                        Embedding = new SqlVector<float>(result.Embedding),
                        Text = result.Text,
                        DateCreated = date,
                        DateModified = date
                    });
                }
            }
            _jobStatusStore.Update(jobId, JobStatus.Embedding, 75);

            var document = new ConversationDocument
            {
                UserId = userId,
                ConversationId = chatId,
                Name = fileDataDto.FileName,
                Extension = GetFileExtension(fileDataDto.FileName),
                MimeType = fileDataDto.ContentType,
                Size = fileDataDto.Length,
                Path = blob,
                Pages = documentPages,
                DateCreated = date,
                DateModified = date
            };

            await _ctx.AddAsync(document, cancellationToken);
            await _ctx.SaveChangesAsync(cancellationToken);

            _jobStatusStore.Update(jobId, JobStatus.Processed, 100);

            return document.MapToChatDocumentDto();
        }
        

        public async Task<PageEmbeddingDto> GeneratePageEmbeddingAsync(DocumentExtractorDto documentExtractor, CancellationToken cancellationToken)
        {
            var embedding = await _embeddingGenerator.GenerateVectorAsync(string.IsNullOrWhiteSpace(documentExtractor.PageText) ? "EMPTY PAGE" : documentExtractor.PageText, null, cancellationToken);
            return new PageEmbeddingDto
            {
                Number = documentExtractor.PageNumber,
                Embedding = embedding,
                Text = documentExtractor.PageText
            };
        }

        #region Static methods
        /// <summary>
        /// Gets the file extension from the provided file name.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>The file extension.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the file name is null or empty.</exception>
        public static string GetFileExtension(string fileName)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName, nameof(fileName));
            var extension = Path.GetExtension(fileName);
            return extension;
        }


        /// <summary>
        /// Extracts text from a PDF file asynchronously.
        /// </summary>
        /// <param name="bytes">The byte array representing the PDF file.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the extracted text from the PDF file.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the byte array is null.</exception>
        public async Task<List<DocumentExtractorDto>> ExtractTextAsync(FileDto fileDto, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(fileDto, nameof(fileDto));

            List<DocumentExtractorDto> dto = [];
            if (fileDto.FileExtension == FileExtensions.Doc || 
                     fileDto.FileExtension == FileExtensions.Docx ||
                     fileDto.FileExtension == FileExtensions.Pdf ||
                     fileDto.FileExtension == FileExtensions.Pptx)
            {
                var analyzeResult = await _documentIntelligenceService.ReadAsync(fileDto.Content, cancellationToken);
                dto = [.. analyzeResult.Pages.Select(page => new DocumentExtractorDto
                    {
                        PageNumber = page.PageNumber,
                        PageText = string.Join("\n", page.Lines.Select(line => line.Content))
                    })];
            }
            else
            {
                throw new InvalidOperationException("Extension not supported for text extraction.");
            }

            return dto;
        }
        #endregion
    }
}
