using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Returns a vector the test chooses, so the distances SQL Server computes are exact and the
/// assertions can name them.
/// </summary>
internal sealed class ScriptedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public float[] Vector { get; set; } = SeedChunk.UnitVector(0);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(Vector))));

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}
