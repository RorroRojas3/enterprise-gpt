using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Documents.Exceptions;

/// <summary>
/// Thrown when blob storage cannot be used as configured: a container is not named, or the client
/// cannot sign a download link; answered with 503.
/// </summary>
/// <remarks>
/// Signing needs a shared-key connection string; a deployment moving to <c>DefaultAzureCredential</c>
/// has to switch to a user-delegation SAS instead.
/// </remarks>
public class StorageNotConfiguredException()
    : ProblemException(
        HttpStatusCode.ServiceUnavailable,
        ProblemCodes.StorageNotConfigured,
        "Document storage is not configured correctly.")
{
}
