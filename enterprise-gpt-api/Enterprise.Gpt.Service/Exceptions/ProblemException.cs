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
public abstract class ProblemException(
    HttpStatusCode statusCode, string? problemCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>
    /// A <see cref="ProblemCodes"/> value, or <see langword="null"/> for the RFC 9110 default type.
    /// </summary>
    public string? ProblemCode { get; } = problemCode;

    /// <summary>Members written into the problem beside the standard fields.</summary>
    public virtual IReadOnlyDictionary<string, object?> Extensions => ReadOnlyDictionary<string, object?>.Empty;
}
