using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Deterministic <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> so upload tests can run the real
/// ingestion pipeline without calling Azure OpenAI.
/// </summary>
/// <remarks>
/// Vectors are <see cref="Dimensions"/> long because SQL Server rejects anything but the
/// <c>vector(1536)</c> column length at save time.
/// </remarks>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 1536;

    public int BatchCount { get; private set; }

    public TaskCompletionSource FirstBatchStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? HoldFirstBatch { get; set; }

    public void Reset()
    {
        BatchCount = 0;
        HoldFirstBatch = null;
        FirstBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        BatchCount++;

        var hold = HoldFirstBatch;
        if (hold is not null && BatchCount == 1)
        {
            FirstBatchStarted.TrySetResult();

            // With the token, so a cancel while parked here unblocks the job instead of hanging the test.
            await hold.Task.WaitAsync(cancellationToken);
        }

        // Derived from the input so a test comparing different chunks cannot pass by accident.
        var embeddings = values.Select(value =>
        {
            var vector = new float[Dimensions];
            vector[0] = value.Length;
            vector[1] = value.GetHashCode(StringComparison.Ordinal) % 1000 / 1000f;
            return new Embedding<float>(vector);
        });

        return [.. embeddings];
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
