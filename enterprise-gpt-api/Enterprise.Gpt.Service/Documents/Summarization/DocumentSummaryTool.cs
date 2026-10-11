using System.ComponentModel;
using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Enterprise.Gpt.Service.Documents.Retrieval.Models;
using Enterprise.Gpt.Service.Documents.Summarization.Models;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Exposes document summarization to the assistant as a callable function.
/// </summary>
/// <remarks>
/// Built per turn over that turn's resolved corpus, so it only summarizes documents the caller can already
/// read. Unlike <see cref="DocumentTool"/> a miss spends real money, so an ambiguous name refuses rather
/// than widening.
/// </remarks>
public static partial class DocumentSummaryTool
{
    /// <summary>The name the model, the activity feed and the usage audit know this tool by.</summary>
    /// <remarks>
    /// Shares <see cref="DocumentTool.ToolName"/>'s leading segment: usage is attributed to an MCP server by
    /// the longest <c>{server}_</c> prefix, so <c>summarize_document</c> would credit a server "summarize".
    /// </remarks>
    public const string ToolName = "document_summarize";

    private const string ToolDescription =
        "Produces a summary of the documents attached to this conversation. Pass a file name to summarize " +
        "one document; omit it to summarize every attached document at once. Use this when the user asks " +
        "what a document says, what it is about, or for an overview of their files — not when they ask a " +
        "specific question about their contents, which " + DocumentTool.ToolName + " answers better and more " +
        "cheaply. The first summary of a document takes a while; later ones are instant.";

    private sealed partial class DocumentSummaryInvoker(
        DocumentRetrievalScope scope,
        Guid conversationId,
        IDocumentSummaryService summaryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        private readonly DocumentRetrievalScope _scope = scope;
        private readonly Guid _conversationId = conversationId;
        private readonly IDocumentSummaryService _summaryService = summaryService;
        private readonly int _toolTimeoutSeconds = toolTimeoutSeconds;
        private readonly ILogger _logger = logger;

        /// <remarks>
        /// Reports <see cref="ChatProgress"/> because a first summary runs inline on the turn, and a silent
        /// tool would look like a stalled answer.
        /// </remarks>
        public async Task<DocumentSummaryToolResult> SummarizeAsync(
            [Description("The file name of the document to summarize. Omit it to summarize every document in the conversation at once.")]
            string? documentName = null,
            CancellationToken cancellationToken = default)
        {
            // Bounds the whole invocation; SummarizationOptions.CallTimeoutSeconds bounds each call in it.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_toolTimeoutSeconds));

            try
            {
                return string.IsNullOrWhiteSpace(documentName)
                    ? await SummarizeAllAsync(deadline.Token).ConfigureAwait(false)
                    : await SummarizeOneAsync(documentName.Trim(), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn is being torn down; a tool error would only invite the model to work around it.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Our deadline, not the caller's: reported to the model, since the user can be told.
                LogSummarizationTimedOut(_logger, ex, _conversationId, _toolTimeoutSeconds);

                throw new InvalidOperationException(
                    "Summarizing took too long and was stopped. Tell the user the document was too large to "
                        + "summarize in a conversation, and offer to answer specific questions about it instead.");
            }
            catch (ValidationException ex)
            {
                // On this path a ValidationException already carries a sanitized, user-facing sentence.
                var message = ex.Errors.FirstOrDefault()?.ErrorMessage ?? "This document could not be summarized.";

                LogSummarizationRefused(_logger, _conversationId, message);

                throw new InvalidOperationException(
                    $"{message} Tell the user this, and do not try to summarize the same thing again.");
            }
            catch (Exception ex)
            {
                // Detailed errors are on, so whatever is thrown reaches the model verbatim: log the real
                // exception and give the model a sentence with nothing about the machinery.
                LogSummarizationFailed(_logger, ex, _conversationId);

                throw new InvalidOperationException(
                    "The summary could not be produced. Tell the user their documents could not be summarized, "
                        + "and offer to answer specific questions about them instead.");
            }
        }

        #region Private methods

        private async Task<DocumentSummaryToolResult> SummarizeOneAsync(
            string documentName, CancellationToken cancellationToken)
        {
            var matches = DocumentRetrievalService.MatchByName(_scope, documentName);

            if (matches.Count != 1)
            {
                // Refused before any model call: a near-miss must not spend a run on the wrong document,
                // or on all of them, when asking again costs one cheap turn.
                return new DocumentSummaryToolResult
                {
                    Scope = "document",
                    DocumentCount = 0,
                    AvailableDocuments = _scope.DocumentNames,
                    Note = matches.Count == 0
                        ? $"No document is named '{documentName}'. Ask again with one of the available names, "
                            + "or omit the name to summarize all of them."
                        : $"'{documentName}' matches more than one document. Ask again with a full name."
                };
            }

            var document = matches[0];

            ChatProgress.Report($"Summarizing {document.Name}…");

            var summary = await _summaryService
                .GetOrCreateAsync(_conversationId, document.Id, document.Source, cancellationToken)
                .ConfigureAwait(false);

            ChatProgress.Report(summary.FromCache ? "Reusing an existing summary" : "Summary ready");

            return new DocumentSummaryToolResult
            {
                Scope = "document",
                DocumentName = document.Name,
                DocumentCount = 1,
                Summary = summary.Text
            };
        }

        private async Task<DocumentSummaryToolResult> SummarizeAllAsync(CancellationToken cancellationToken)
        {
            // One document is its own digest; the digest path would pay a second call to reduce it to itself.
            if (_scope.Documents.Count == 1)
            {
                return await SummarizeOneAsync(_scope.Documents[0].Name, cancellationToken).ConfigureAwait(false);
            }

            ChatProgress.Report(
                $"Summarizing {_scope.Documents.Count} documents…", progress: 0, progressTotal: _scope.Documents.Count);

            var digest = await _summaryService
                .CreateDigestAsync(_conversationId, _scope, cancellationToken)
                .ConfigureAwait(false);

            ChatProgress.Report("Summary ready");

            return new DocumentSummaryToolResult
            {
                Scope = "digest",
                DocumentCount = _scope.Documents.Count,
                Summary = digest
            };
        }

        #endregion

        #region Logging

        [LoggerMessage(Level = LogLevel.Warning, Message = "Summarization for conversation {ConversationId} exceeded its {TimeoutSeconds}s tool deadline.")]
        private static partial void LogSummarizationTimedOut(
            ILogger logger, Exception exception, Guid conversationId, int timeoutSeconds);

        [LoggerMessage(Level = LogLevel.Information, Message = "Summarization refused for conversation {ConversationId}: {Reason}")]
        private static partial void LogSummarizationRefused(ILogger logger, Guid conversationId, string reason);

        [LoggerMessage(Level = LogLevel.Error, Message = "Summarization failed for conversation {ConversationId}.")]
        private static partial void LogSummarizationFailed(ILogger logger, Exception exception, Guid conversationId);

        #endregion
    }

    #region Public static methods

    /// <summary>
    /// Builds the summarization function for one turn, billing its model calls to
    /// <paramref name="conversationId"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toolTimeoutSeconds"/> is not positive.</exception>
    public static AIFunction Create(
        DocumentRetrievalScope scope,
        Guid conversationId,
        IDocumentSummaryService summaryService,
        int toolTimeoutSeconds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(summaryService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toolTimeoutSeconds);

        var invoker = new DocumentSummaryInvoker(scope, conversationId, summaryService, toolTimeoutSeconds, logger);

        return AIFunctionFactory.Create(invoker.SummarizeAsync, ToolName, ToolDescription);
    }

    #endregion
}
