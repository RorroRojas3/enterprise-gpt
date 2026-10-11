using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents.Chunking;
using Enterprise.Gpt.Service.Documents.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Service.Documents.Summarization;

/// <summary>
/// Reassembles a document's text from the chunk rows ingestion already wrote.
/// </summary>
public interface IDocumentTextAssembler
{
    /// <summary>
    /// Reads a document's chunks in order and joins them back into one string.
    /// </summary>
    /// <returns>
    /// The reassembled text, or an empty string when the document has no active chunk rows.
    /// </returns>
    /// <remarks>
    /// Read from chunks, not the blob, so Document Intelligence is not re-billed. Every word survives, but
    /// whitespace formatting may not: the chunker consumes the whitespace it splits on.
    /// </remarks>
    Task<string> AssembleAsync(Guid documentId, DocumentSources source, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DocumentTextAssembler(
    EnterpriseGptDbContext ctx,
    ITextChunker textChunker) : IDocumentTextAssembler
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly ITextChunker _textChunker = textChunker;

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        Guid documentId,
        DocumentSources source,
        CancellationToken cancellationToken)
    {
        var texts = await ReadChunkTextsAsync(documentId, source, cancellationToken)
            .ConfigureAwait(false);

        if (texts.Count == 0)
        {
            return string.Empty;
        }

        // As in DocumentRetrievalService: overlap is configured in tokens, the seam search works in chars.
        var maxOverlapChars = Math.Max(64, _textChunker.OverlapTokens * 6);

        var accumulated = new StringBuilder(texts[0]);

        for (var i = 1; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentRetrievalService.AppendWithoutOverlap(accumulated, texts[i], maxOverlapChars);
        }

        return accumulated.ToString();
    }

    #region Private methods

    /// <remarks>
    /// Projects to the text alone: each chunk row also carries a <c>vector(1536)</c> embedding.
    /// </remarks>
    private async Task<List<string>> ReadChunkTextsAsync(
        Guid documentId,
        DocumentSources source,
        CancellationToken cancellationToken) =>
        source switch
        {
            DocumentSources.Conversation => await _ctx.ConversationDocumentChunks
                .AsNoTracking()
                // The navigation is what applies the document's own soft-delete filter.
                .Where(c => c.ConversationDocumentId == documentId
                    && !c.ConversationDocument.DateDeactivated.HasValue)
                .OrderBy(c => c.Index)
                .Select(c => c.Text)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),

            DocumentSources.Project => await _ctx.ProjectDocumentChunks
                .AsNoTracking()
                .Where(c => c.ProjectDocumentId == documentId
                    && !c.ProjectDocument.DateDeactivated.HasValue)
                .OrderBy(c => c.Index)
                .Select(c => c.Text)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };

    #endregion
}
