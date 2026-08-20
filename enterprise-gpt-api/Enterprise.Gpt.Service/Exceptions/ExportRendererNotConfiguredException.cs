using Enterprise.Gpt.Service.Export;

namespace Enterprise.Gpt.Service.Exceptions;

/// <summary>
/// Thrown when a conversation export is asked for in a format this deployment has no renderer for —
/// the format is disabled by configuration, or its renderer could not be built (PDF, when no usable
/// font was found). Mapped to HTTP 503 by the global exception handler: the request is well formed and
/// the conversation exists, so this is an operator condition rather than a client mistake.
/// </summary>
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/> on
/// purpose — the handler maps the latter to 400, which would report a deployment gap as a bad request
/// and send the client into a retry loop over a state no retry can change. The distinct problem type
/// is what lets the client say "not available here" instead of "try again", which is the whole point
/// of US-1501's fifth criterion.
/// </remarks>
/// <param name="format">The format that has no renderer.</param>
public class ExportRendererNotConfiguredException(ConversationExportFormats format)
    : Exception($"Conversation export to {format.ToWireFormat()} is not available in this environment.")
{
    /// <summary>
    /// Gets the format that has no renderer, as it travels on the wire.
    /// </summary>
    /// <remarks>
    /// Surfaced as the problem's <c>format</c> extension, so a client showing a three-item download
    /// menu can disable the one item rather than the whole control.
    /// </remarks>
    public string Format { get; } = format.ToWireFormat();
}
