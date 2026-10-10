using System.Net;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a well-formed request conflicts with the current state of a resource; answered with 409.
/// </summary>
public class ConflictException : ProblemException
{
    /// <summary>Initializes a new instance of the <see cref="ConflictException"/> class.</summary>
    public ConflictException(string message)
        : base(HttpStatusCode.Conflict, problemCode: null, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConflictException"/> class.</summary>
    public ConflictException(string message, Exception innerException)
        : base(HttpStatusCode.Conflict, problemCode: null, message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ConflictException"/> class with its own problem type.</summary>
    protected ConflictException(string? problemCode, string message, Exception? innerException = null)
        : base(HttpStatusCode.Conflict, problemCode, message, innerException)
    {
    }
}
