using System.Collections.ObjectModel;
using System.Net;
using Enterprise.Gpt.Common.Constants;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// An exception the API answers with its own status code and, when <see cref="ProblemCode"/> is set,
/// an application-specific problem type.
/// </summary>
/// <remarks>
/// The message reaches the caller as <c>detail</c>, so it must never carry PII, secrets, or anything
/// about another user's data.
/// </remarks>
public abstract class ProblemException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProblemException"/> class.
    /// </summary>
    /// <param name="statusCode">The status code the API responds with.</param>
    /// <param name="problemCode">A <see cref="ProblemCodes"/> value, or <see langword="null"/> for the RFC 9110 default type.</param>
    /// <param name="message">The caller-facing detail.</param>
    /// <param name="innerException">The fault being translated, if any.</param>
    protected ProblemException(HttpStatusCode statusCode, string? problemCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ProblemCode = problemCode;
    }

    /// <summary>Gets the status code the API responds with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets the <see cref="ProblemCodes"/> value naming the problem type, if the problem has one.</summary>
    public string? ProblemCode { get; }

    /// <summary>Gets the members written into the problem beside the standard fields.</summary>
    public virtual IReadOnlyDictionary<string, object?> Extensions => ReadOnlyDictionary<string, object?>.Empty;
}
