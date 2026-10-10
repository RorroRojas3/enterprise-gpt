namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// An <see cref="IProgress{T}"/> that records reports synchronously on the calling thread.
/// </summary>
/// <remarks>
/// <see cref="Progress{T}"/> posts to the thread pool, so a test using it would have to wait for reports to
/// arrive and could still observe them out of order. Capturing inline makes the assertions deterministic.
/// </remarks>
/// <typeparam name="T">The progress payload type.</typeparam>
/// <param name="reports">The list reports are appended to.</param>
public sealed class CollectingProgress<T>(IList<T> reports) : IProgress<T>
{
    private readonly IList<T> _reports = reports;

    /// <inheritdoc />
    public void Report(T value) => _reports.Add(value);
}
