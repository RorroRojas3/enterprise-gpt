using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Enterprise.Gpt.Service
{
    public interface IDocumentIntelligenceService
    {
        /// <summary>
        /// Analyzes the provided document bytes with the configured OCR model and returns the analysis result.
        /// </summary>
        /// <param name="bytes">The binary content of the document to analyze. Must not be <see langword="null"/>.</param>
        /// <param name="heartbeat">
        /// Optional callback invoked once per poll with the time elapsed so far. Analysis is a long-running
        /// server-side operation with no intermediate progress, so this exists purely to let callers show
        /// that work is still happening; it is never a completion percentage.
        /// </param>
        /// <param name="cancellationToken">A token to observe while waiting for the operation to complete.</param>
        /// <returns>The <see cref="AnalyzeResult"/> produced by Azure Document Intelligence.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is <see langword="null"/>.</exception>
        /// <exception cref="RequestFailedException">Thrown when the Azure Document Intelligence service returns an error.</exception>
        Task<AnalyzeResult> ReadAsync(byte[] bytes, Action<TimeSpan>? heartbeat, CancellationToken cancellationToken);
    }

    public class DocumentIntelligenceService(ILogger<DocumentIntelligenceService> logger,
        IConfiguration configuration,
        DocumentIntelligenceClient client) : IDocumentIntelligenceService
    {
        // Matches the service's own recommended polling cadence closely enough to keep the status feed
        // lively without adding meaningful request volume to a call that runs for tens of seconds.
        private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

        private readonly ILogger<DocumentIntelligenceService> _logger = logger;
        private readonly DocumentIntelligenceClient _client = client;
        private readonly string _ocrModelId = configuration.GetValue<string>("DocumentIntelligence:OCRModelId")!;

        // An analysis that never reaches a terminal state would otherwise hold a background-processing slot
        // — there are only a handful — until the host shuts down, and its job would never become terminal
        // so its status snapshot would never be evicted either.
        private readonly TimeSpan _analysisTimeout =
            configuration.GetValue<TimeSpan?>("DocumentIntelligence:AnalysisTimeout") ?? TimeSpan.FromMinutes(10);

        /// <inheritdoc />
        public async Task<AnalyzeResult> ReadAsync(byte[] bytes, Action<TimeSpan>? heartbeat, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            var binaryData = BinaryData.FromBytes(bytes);
            var options = new AnalyzeDocumentOptions(_ocrModelId, binaryData);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_analysisTimeout);

            // WaitUntil.Started rather than Completed so the operation can be polled here: the SDK's own
            // wait loop is opaque, and the caller needs to keep the job status moving while OCR runs.
            var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Started, options, timeout.Token);

            var stopwatch = Stopwatch.StartNew();
            while (!operation.HasCompleted)
            {
                try
                {
                    await Task.Delay(_pollInterval, timeout.Token);
                    await operation.UpdateStatusAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Document analysis with model '{ModelId}' did not complete within {Timeout}", _ocrModelId, _analysisTimeout);
                    throw new TimeoutException($"Document analysis did not complete within {_analysisTimeout}.");
                }

                heartbeat?.Invoke(stopwatch.Elapsed);
            }

            _logger.LogInformation("Document analyzed successfully with model '{ModelId}' in {ElapsedMs} ms",
                _ocrModelId, stopwatch.ElapsedMilliseconds);

            return operation.Value;
        }
    }
}
