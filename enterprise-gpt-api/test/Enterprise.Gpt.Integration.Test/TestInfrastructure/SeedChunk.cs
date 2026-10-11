namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// One chunk to seed, with the embedding retrieval finds it by. <c>Index</c> must be unique per document;
/// <see cref="UnitVector"/> gives exact distances.
/// </summary>
public sealed record SeedChunk(int Index, string Text, float[] Vector, int? SourceNumber = null)
{
    /// <summary>The <c>Embedding</c> column length; SQL Server rejects any other.</summary>
    public const int Dimensions = 1536;

    /// <summary>The same axis is cosine distance 0 away, a different axis 1.</summary>
    public static float[] UnitVector(int axis)
    {
        var vector = new float[Dimensions];
        vector[axis] = 1f;

        return vector;
    }

    /// <summary>
    /// A unit vector at cosine distance <c>1 - similarity</c> from <c>UnitVector(axis)</c>.
    /// </summary>
    public static float[] Blend(int axis, int otherAxis, double similarity)
    {
        var vector = new float[Dimensions];
        vector[axis] = (float)similarity;
        vector[otherAxis] = (float)Math.Sqrt(1.0 - (similarity * similarity));

        return vector;
    }
}
