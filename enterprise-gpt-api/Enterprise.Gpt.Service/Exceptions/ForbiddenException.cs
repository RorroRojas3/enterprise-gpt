using System.Net;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when the caller is authenticated but lacks the permission the resource requires, such as an
/// MCP server's own grant; answered with 403.
/// </summary>
public class ForbiddenException : ProblemException
{
    /// <summary>Initializes a new instance of the <see cref="ForbiddenException"/> class.</summary>
    public ForbiddenException(string message)
        : base(HttpStatusCode.Forbidden, ProblemCodes.Forbidden, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ForbiddenException"/> class.</summary>
    public ForbiddenException(string message, Exception innerException)
        : base(HttpStatusCode.Forbidden, ProblemCodes.Forbidden, message, innerException)
    {
    }
}
