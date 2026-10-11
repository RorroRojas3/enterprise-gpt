using System.Diagnostics;
using Azure;
using Azure.AI.DocumentIntelligence;
using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Extraction;

public interface IDocumentIntelligenceService
{
    /// <summary>
    /// Analyzes the provided document bytes with the configured OCR model and returns the analysis result.
    /// </summary>
    /// <remarks>
    /// <paramref name="heartbeat"/> gets the elapsed time once per poll: a liveness signal, never a
    /// percentage, since analysis reports no intermediate progress.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is <see langword="null"/>.</exception>
    /// <exception cref="RequestFailedException">The service returned an error.</exception>
    Task<AnalyzeResult> ReadAsync(byte[] bytes, Action<TimeSpan>? heartbeat, CancellationToken cancellationToken);
}

public partial class DocumentIntelligenceService(ILogger<DocumentIntelligenceService> logger,
    IOptions<DocumentIntelligenceOptions> options,
    DocumentIntelligenceClient client) : IDocumentIntelligenceService
{
    // Close to the service's recommended polling cadence.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<DocumentIntelligenceService> _logger = logger;
    private readonly DocumentIntelligenceClient _client = client;
    private readonly string _ocrModelId = options.Value.OCRModelId;

    // A stuck analysis would otherwise hold one of a few background slots until shutdown, and its job
    // status would never be evicted.
    private readonly TimeSpan _analysisTimeout = options.Value.AnalysisTimeout;

    /// <inheritdoc />
    public async Task<AnalyzeResult> ReadAsync(byte[] bytes, Action<TimeSpan>? heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var binaryData = BinaryData.FromBytes(bytes);
        var options = new AnalyzeDocumentOptions(_ocrModelId, binaryData);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_analysisTimeout);

        // Started, not Completed: the SDK's wait loop is opaque, and polling here keeps the job status moving.
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
                LogAnalysisTimedOut(_logger, _ocrModelId, _analysisTimeout);
                throw new TimeoutException($"Document analysis did not complete within {_analysisTimeout}.");
            }

            heartbeat?.Invoke(stopwatch.Elapsed);
        }

        LogAnalysisCompleted(_logger, _ocrModelId, stopwatch.ElapsedMilliseconds);

        return operation.Value;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document analysis with model '{ModelId}' did not complete within {Timeout}")]
    private static partial void LogAnalysisTimedOut(ILogger logger, string modelId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document analyzed successfully with model '{ModelId}' in {ElapsedMs} ms")]
    private static partial void LogAnalysisCompleted(ILogger logger, string modelId, long elapsedMs);

    #endregion
}
