using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RR.AI_Chat.Service
{
    public interface IDocumentIntelligenceService
    {
        /// <summary>
        /// Analyzes the provided document bytes with the configured OCR model and returns the analysis result.
        /// </summary>
        /// <param name="bytes">The binary content of the document to analyze. Must not be <see langword="null"/>.</param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The <see cref="AnalyzeResult"/> produced by Azure Document Intelligence.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is <see langword="null"/>.</exception>
        /// <exception cref="RequestFailedException">Thrown when the Azure Document Intelligence service returns an error.</exception>
        Task<AnalyzeResult> ReadAsync(byte[] bytes, CancellationToken cancellationToken);
    }

    public class DocumentIntelligenceService(ILogger<DocumentIntelligenceService> logger,
        IConfiguration configuration,
        DocumentIntelligenceClient client) : IDocumentIntelligenceService
    {
        private readonly ILogger<DocumentIntelligenceService> _logger = logger;
        private readonly DocumentIntelligenceClient _client = client;
        private readonly string _ocrModelId = configuration.GetValue<string>("DocumentIntelligence:OCRModelId")!;

        /// <inheritdoc />
        public async Task<AnalyzeResult> ReadAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            var binaryData = BinaryData.FromBytes(bytes);
            var options = new AnalyzeDocumentOptions(_ocrModelId, binaryData);

            var result = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, options, cancellationToken);

            _logger.LogInformation("Document analyzed successfully with model '{ModelId}'", _ocrModelId);

            return result.Value;
        }
    }
}
