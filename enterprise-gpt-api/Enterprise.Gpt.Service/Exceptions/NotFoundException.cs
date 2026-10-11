using System.Net;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a resource does not exist, is deactivated, or does not belong to the caller; answered with 404.
/// </summary>
public class NotFoundException : ProblemException
{
    public NotFoundException(string message)
        : base(HttpStatusCode.NotFound, ProblemCodes.ResourceNotFound, message)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(HttpStatusCode.NotFound, ProblemCodes.ResourceNotFound, message, innerException)
    {
    }
}
