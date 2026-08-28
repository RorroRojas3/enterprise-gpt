namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when blob storage cannot be used as configured — a container is not named, or
/// the <c>BlobServiceClient</c> was built with credentials that cannot produce a service SAS. Mapped
/// to HTTP 503 by the global exception handler: the request is well formed and the document exists,
/// so this is an operator condition rather than a client mistake.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> on
/// purpose — the handler maps the latter to 400, which would misreport a configuration gap as a bad
/// request. Signing needs a shared-key connection string; a deployment moving to
/// <c>DefaultAzureCredential</c> has to switch to a user-delegation SAS instead. The message names no
/// account, container or credential, so it is safe to return to the caller.
/// </remarks>
public class StorageNotConfiguredException()
    : Exception("Document storage is not configured correctly.")
{
}
