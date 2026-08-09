namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// One chunk to seed into a document, with the embedding retrieval should find it by.
/// </summary>
/// <param name="Index">The chunk's zero-based ordinal within its document. Must be unique per document.</param>
/// <param name="Text">The chunk text, which is what the keyword pass matches and what a passage is built from.</param>
/// <param name="Vector">The 1536-dimension embedding. Use <see cref="UnitVector"/> to get predictable distances.</param>
/// <param name="SourceNumber">The page or slide the chunk starts on, or <see langword="null"/> for a format with no pages.</param>
public sealed record SeedChunk(int Index, string Text, float[] Vector, int? SourceNumber = null)
{
    /// <summary>
    /// Dimension of the <c>Embedding</c> column. SQL Server rejects any other length.
    /// </summary>
    public const int Dimensions = 1536;

    /// <summary>
    /// Builds a unit vector along one axis, so cosine distances between seeded chunks are exact and
    /// obvious: the same axis gives 0, a different axis gives 1, and <see cref="Blend"/> gives what it
    /// is asked for.
    /// </summary>
    /// <param name="axis">The dimension to set to one.</param>
    /// <returns>The vector.</returns>
    public static float[] UnitVector(int axis)
    {
        var vector = new float[Dimensions];
        vector[axis] = 1f;

        return vector;
    }

    /// <summary>
    /// Builds a unit vector at a chosen cosine similarity to <c>UnitVector(axis)</c>, by mixing in a
    /// second, orthogonal axis.
    /// </summary>
    /// <param name="axis">The axis to be similar to.</param>
    /// <param name="otherAxis">An orthogonal axis to mix in.</param>
    /// <param name="similarity">The cosine similarity to <c>UnitVector(axis)</c>, from 0 to 1.</param>
    /// <returns>The vector, whose cosine distance from <c>UnitVector(axis)</c> is <c>1 - similarity</c>.</returns>
    public static float[] Blend(int axis, int otherAxis, double similarity)
    {
        var vector = new float[Dimensions];
        vector[axis] = (float)similarity;
        vector[otherAxis] = (float)Math.Sqrt(1.0 - (similarity * similarity));

        return vector;
    }
}
