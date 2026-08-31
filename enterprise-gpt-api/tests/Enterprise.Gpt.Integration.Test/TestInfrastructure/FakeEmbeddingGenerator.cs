using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Deterministic <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> so upload tests can run the real
/// ingestion pipeline without calling Azure OpenAI.
/// </summary>
/// <remarks>
/// Vectors are <see cref="Dimensions"/> long because the <c>Embedding</c> column is declared
/// <c>vector(1536)</c>; SQL Server rejects any other length, so a shorter stub would fail at save time
/// rather than exercising persistence.
/// </remarks>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>
    /// Dimension of the generated vectors, matching the database column.
    /// </summary>
    public const int Dimensions = 1536;

    /// <summary>
    /// Gets the number of batches requested, so tests can assert that chunks are embedded in batches
    /// rather than one request per chunk.
    /// </summary>
    public int BatchCount { get; private set; }

    /// <summary>Completes once the first batch has been asked for, so a test can act mid-pipeline.</summary>
    public TaskCompletionSource FirstBatchStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Holds the first batch until a test releases it. Left null by default so the ordinary upload tests
    /// run unblocked.
    /// </summary>
    public TaskCompletionSource? HoldFirstBatch { get; set; }

    /// <summary>Clears the counters and the latch between tests.</summary>
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

            // Awaited *with* the token, so a cancel while the job is parked here unblocks it rather
            // than hanging the test until its own timeout.
            await hold.Task.WaitAsync(cancellationToken);
        }

        // Derived from the input so different chunks get different vectors, which keeps a test that
        // accidentally compares them from passing for the wrong reason.
        var embeddings = values.Select(value =>
        {
            var vector = new float[Dimensions];
            vector[0] = value.Length;
            vector[1] = value.GetHashCode(StringComparison.Ordinal) % 1000 / 1000f;
            return new Embedding<float>(vector);
        });

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
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
