namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// An <see cref="IProgress{T}"/> that records reports synchronously on the calling thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to the thread pool, so reports could arrive late or out of order.
/// </remarks>
public sealed class CollectingProgress<T>(IList<T> reports) : IProgress<T>
{
    private readonly IList<T> _reports = reports;

    /// <inheritdoc />
    public void Report(T value) => _reports.Add(value);
}
