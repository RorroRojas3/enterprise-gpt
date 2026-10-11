using System.Net;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// The outcome of a transactional batch.
/// </summary>
/// <param name="IsSuccess">Whether every operation was applied.</param>
/// <param name="StatusCode">The batch's overall status.</param>
/// <param name="FailedOperationIndex">
/// Zero-based index of the failing operation; <see langword="null"/> on success or when none can be blamed.
/// </param>
/// <param name="FailedOperationStatusCode">
/// The failing operation's status; <see langword="null"/> on success.
/// </param>
/// <remarks>
/// Identifiable because Cosmos DB marks every other operation <see cref="HttpStatusCode.FailedDependency"/>.
/// </remarks>
public sealed record TranscriptBatchResult(
    bool IsSuccess,
    HttpStatusCode StatusCode,
    int? FailedOperationIndex,
    HttpStatusCode? FailedOperationStatusCode)
{
    /// <summary>
    /// Describes the failure for a log message, without including any document content.
    /// </summary>
    public string Describe()
    {
        return IsSuccess
            ? $"batch succeeded ({(int)StatusCode})"
            : FailedOperationIndex is { } index
                ? $"batch failed at operation {index} with {(int)(FailedOperationStatusCode ?? StatusCode)}"
                : $"batch failed with {(int)StatusCode}";
    }
}
