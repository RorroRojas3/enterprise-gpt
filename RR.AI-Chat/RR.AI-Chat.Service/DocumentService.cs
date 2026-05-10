using Microsoft.Data.SqlTypes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RR.AI_Chat.Common.Extensions;
using RR.AI_Chat.Dto;
using RR.AI_Chat.Dto.Enums;
using RR.AI_Chat.Entity;
using RR.AI_Chat.Repository;
using RR.AI_Chat.Service.BackgroundJobs;
using RR.AI_Chat.Service.Common.Interface;

namespace RR.AI_Chat.Service
{
    public interface IDocumentService
    {
        Task<ConversationDocumentDto> CreateConversationDocumentAsync(string jobId, FileDto fileDataDto, Guid userId, Guid chatId, CancellationToken cancellationToken);

        Task<FileDto?> GenerateConversationHistoryAsync(Guid chatId, DocumentFormats documentFormat, CancellationToken cancellationToken);

        Task<List<DocumentExtractorDto>> ExtractTextAsync(FileDto fileDto, CancellationToken cancellationToken);

        Task<PageEmbeddingDto> GeneratePageEmbeddingAsync(DocumentExtractorDto documentExtractor, CancellationToken cancellationToken);
    }

    public class DocumentService(ILogger<DocumentService> logger,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IBlobStorageService blobStorageService,
        IConfiguration configuration,
        IDocumentIntelligenceService documentIntelligenceService,
        ITokenService tokenService,
        IHtmlService htmlService,
        IPdfService pdfService,
        IWordService wordService,
        IMarkdownService markdownService,
        [FromKeyedServices("excel")] IFileService excelService,
        [FromKeyedServices("common")] IFileService commonFileService,
        [FromKeyedServices("word")] IFileService wordFileService,
        IAzureCosmosService cosmosService,
        IJobStatusStore jobStatusStore,
        AIChatDbContext ctx) : IDocumentService
    {
        private readonly ILogger _logger = logger;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator;
        private readonly IBlobStorageService _blobStorageService = blobStorageService;
        private readonly IConfiguration _configuration = configuration;
        private readonly IDocumentIntelligenceService _documentIntelligenceService = documentIntelligenceService;
        private readonly ITokenService _tokenService = tokenService;
        private readonly IHtmlService _htmlService = htmlService;
        private readonly IPdfService _pdfService = pdfService;
        private readonly IWordService _wordService = wordService;
        private readonly IMarkdownService _markdownService = markdownService;
        private readonly IFileService _excelService = excelService;
        private readonly IFileService _commonFileService = commonFileService;
        private readonly IFileService _wordFileService = wordFileService;
        private readonly IAzureCosmosService _cosmosService = cosmosService;
        private readonly IJobStatusStore _jobStatusStore = jobStatusStore;
        private readonly AIChatDbContext _ctx = ctx;

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

        public async Task<FileDto?> GenerateConversationHistoryAsync(Guid chatId, DocumentFormats documentFormat, CancellationToken cancellationToken)
        {
            var oid = _tokenService.GetOid();

            var chat = await _cosmosService.GetItemAsync<CosmosConversation>(chatId.ToString(), oid.ToString(), cancellationToken);
            if (chat == null)
            {
                throw new InvalidOperationException($"Chat with id {chatId} not found.");
            }

            var html = _htmlService.GenerateConversationHistoryAsync(chat.Messages);
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            byte[]? bytes = documentFormat switch
            {
                DocumentFormats.Pdf => _pdfService.GeneratePdfFromHtml(html),
                DocumentFormats.Word => _wordService.GenerateWordFromHtml(html),
                DocumentFormats.Markdown => _markdownService.GenerateMarkdownFromHtml(html),
                _ => null
            };
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var fileName = documentFormat switch
            {
                DocumentFormats.Pdf => $"conversation-history-{chatId}.pdf",
                DocumentFormats.Word => $"conversation-history-{chatId}.docx",
                DocumentFormats.Markdown => $"conversation-history-{chatId}.md",
                _ => null
            };
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            return new FileDto
            {
                FileName = fileName,
                Content = bytes,
                ContentType = documentFormat.GetDescription(),
                Length = bytes.Length
            };
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
            if (fileDto.FileExtension == FileExtensions.Cs ||
                fileDto.FileExtension == FileExtensions.Csproj ||
                fileDto.FileExtension == FileExtensions.Css ||
                fileDto.FileExtension == FileExtensions.Html ||
                fileDto.FileExtension == FileExtensions.Js ||
                fileDto.FileExtension == FileExtensions.Json ||
                fileDto.FileExtension == FileExtensions.Jsx ||
                fileDto.FileExtension == FileExtensions.Log ||
                fileDto.FileExtension == FileExtensions.Md ||
                fileDto.FileExtension == FileExtensions.Ps1 ||
                fileDto.FileExtension == FileExtensions.Py ||
                fileDto.FileExtension == FileExtensions.Scss ||
                fileDto.FileExtension == FileExtensions.Sql ||
                fileDto.FileExtension == FileExtensions.Ts ||
                fileDto.FileExtension == FileExtensions.Tsx ||
                fileDto.FileExtension == FileExtensions.Txt ||
                fileDto.FileExtension == FileExtensions.Xml ||
                fileDto.FileExtension == FileExtensions.Yaml ||
                fileDto.FileExtension == FileExtensions.Yml)
            {
                dto = _commonFileService.ExtractText(fileDto.Content, fileDto.FileName);
            }
            else if (fileDto.FileExtension == FileExtensions.Csv ||
                fileDto.FileExtension == FileExtensions.Xls ||
                fileDto.FileExtension == FileExtensions.Xlsm ||
                fileDto.FileExtension == FileExtensions.Xlsx)
            {
                dto = _excelService.ExtractText(fileDto.Content, fileDto.FileName);
            }
            else if (fileDto.FileExtension == FileExtensions.Pdf ||
                     fileDto.FileExtension == FileExtensions.Pptx)
            {
                var analyzeResult = await _documentIntelligenceService.ReadAsync(fileDto.Content, cancellationToken);
                dto = [.. analyzeResult.Pages.Select(page => new DocumentExtractorDto
                    {
                        PageNumber = page.PageNumber,
                        PageText = string.Join("\n", page.Lines.Select(line => line.Content))
                    })];
            }
            else if (fileDto.FileExtension == FileExtensions.Doc ||
                     fileDto.FileExtension == FileExtensions.Docx)
            {
                dto = _wordFileService.ExtractText(fileDto.Content, fileDto.FileName);
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
