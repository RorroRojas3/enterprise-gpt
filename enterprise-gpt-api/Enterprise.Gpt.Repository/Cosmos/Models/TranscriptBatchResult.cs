using System.Net;
using Enterprise.Gpt.Common.Enums;
using Microsoft.Azure.Cosmos;

namespace Enterprise.Gpt.Repository.Cosmos.Models;

/// <summary>
/// The outcome of a transactional batch.
/// </summary>
/// <param name="IsSuccess">Whether every operation was applied.</param>
/// <param name="StatusCode">The status the batch as a whole returned.</param>
/// <param name="FailedOperationIndex">
/// The zero-based index of the operation that caused the failure, or <see langword="null"/> when
/// the batch succeeded or no single operation can be blamed.
/// </param>
/// <param name="FailedOperationStatusCode">
/// The status the failing operation returned, or <see langword="null"/> when the batch succeeded.
/// </param>
/// <remarks>
/// The failing operation is identifiable because Cosmos DB returns that operation's own status and
/// marks every other operation <see cref="HttpStatusCode.FailedDependency"/> (424) — so the one
/// entry whose status is neither success nor 424 is the cause.
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
    /// <returns>A short description of which operation failed and how.</returns>
    public string Describe()
    {
        return IsSuccess
            ? $"batch succeeded ({(int)StatusCode})"
            : FailedOperationIndex is { } index
                ? $"batch failed at operation {index} with {(int)(FailedOperationStatusCode ?? StatusCode)}"
                : $"batch failed with {(int)StatusCode}";
    }
}
