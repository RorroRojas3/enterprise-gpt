using System.Net;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a transactional batch is rejected, naming the operation that caused it.
/// </summary>
/// <remarks>
/// A batch is all-or-nothing, so a failure means nothing in it was written. Cosmos reports the
/// operation that actually failed with its own status and marks every sibling
/// <see cref="HttpStatusCode.FailedDependency"/>; this exception carries the former, because
/// "operation 1 conflicted" is diagnosable where "the batch failed" is not.
/// </remarks>
/// <param name="failedOperationIndex">The zero-based index of the operation that failed.</param>
/// <param name="statusCode">The status Cosmos returned for that operation.</param>
/// <param name="message">The exception message.</param>
public class CosmosBatchFailedException(int failedOperationIndex, HttpStatusCode statusCode, string message)
    : Exception(message)
{
    /// <summary>
    /// The zero-based index of the operation that failed within the submitted batch.
    /// </summary>
    public int FailedOperationIndex { get; } = failedOperationIndex;

    /// <summary>
    /// The status Cosmos returned for the failing operation.
    /// </summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
