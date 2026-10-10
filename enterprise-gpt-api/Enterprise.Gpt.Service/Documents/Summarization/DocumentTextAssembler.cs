using System.Text;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Tool;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Service.Summarization;

/// <summary>
/// Reassembles a document's text from the chunk rows ingestion already wrote.
/// </summary>
public interface IDocumentTextAssembler
{
    /// <summary>
    /// Reads a document's chunks in order and joins them back into one string.
    /// </summary>
    /// <param name="documentId">The document's identifier.</param>
    /// <param name="source">Which document family the id belongs to.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>
    /// The reassembled text, or an empty string when the document has no active chunk rows.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Chunks are the source rather than the blob, because re-extracting a document would re-bill
    /// Azure Document Intelligence for text the database already holds.
    /// </para>
    /// <para>
    /// The result is not guaranteed byte-identical to the original upload. The chunker joins its
    /// units with a newline and its splitting consumes the whitespace it splits on, so blank lines
    /// can collapse and inter-sentence spacing can become newlines. Every word survives; the
    /// original formatting does not.
    /// </para>
    /// </remarks>
    Task<string> AssembleAsync(Guid documentId, DocumentSource source, CancellationToken cancellationToken);
}

/// <summary>
/// Reassembles document text out of <c>Core.ConversationDocumentChunk</c> and
/// <c>Core.ProjectDocumentChunk</c>.
/// </summary>
/// <param name="ctx">The database context.</param>
/// <param name="textChunker">The chunker whose overlap setting produced the seams being stripped.</param>
public sealed class DocumentTextAssembler(
    EnterpriseGptDbContext ctx,
    ITextChunker textChunker) : IDocumentTextAssembler
{
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly ITextChunker _textChunker = textChunker;

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        Guid documentId,
        DocumentSource source,
        CancellationToken cancellationToken)
    {
        var texts = await ReadChunkTextsAsync(documentId, source, cancellationToken)
            .ConfigureAwait(false);

        if (texts.Count == 0)
        {
            return string.Empty;
        }

        // The same conversion DocumentRetrievalService uses when it stitches adjacent chunks back
        // together: overlap is configured in tokens, and the seam search works in characters.
        var maxOverlapChars = Math.Max(64, _textChunker.OverlapTokens * 6);

        var accumulated = new StringBuilder(texts[0]);

        for (var i = 1; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentRetrievalService.AppendWithoutOverlap(accumulated, texts[i], maxOverlapChars);
        }

        return accumulated.ToString();
    }

    /// <remarks>
    /// Projects to the text column alone. A chunk row carries a <c>vector(1536)</c> embedding, so
    /// materialising entities would pull several kilobytes per chunk across the wire for a column
    /// summarization never reads.
    /// </remarks>
    private async Task<List<string>> ReadChunkTextsAsync(
        Guid documentId,
        DocumentSource source,
        CancellationToken cancellationToken) =>
        source switch
        {
            DocumentSource.Conversation => await _ctx.ConversationDocumentChunks
                .AsNoTracking()
                .Where(c => c.ConversationDocumentId == documentId
                    && !c.DateDeactivated.HasValue
                    && !c.ConversationDocument.DateDeactivated.HasValue)
                .OrderBy(c => c.Index)
                .Select(c => c.Text)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),

            DocumentSource.Project => await _ctx.ProjectDocumentChunks
                .AsNoTracking()
                .Where(c => c.ProjectDocumentId == documentId
                    && !c.DateDeactivated.HasValue
                    && !c.ProjectDocument.DateDeactivated.HasValue)
                .OrderBy(c => c.Index)
                .Select(c => c.Text)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown document source.")
        };
}
