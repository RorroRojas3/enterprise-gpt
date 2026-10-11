namespace Enterprise.Gpt.Common.Constants.Documents;

/// <summary>
/// The shape of a stored document chunk.
/// </summary>
/// <remarks>
/// The configured embedding model must return vectors of exactly this size; retrieval refuses any other.
/// </remarks>
public static class DocumentChunkLimits
{
    /// <summary>The dimension of the <c>vector</c> column every chunk embedding is stored in.</summary>
    public const int EmbeddingDimensions = 1536;
}
