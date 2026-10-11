using System.Net;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exceptions;

namespace Enterprise.Gpt.Service.Exports.Exceptions;

/// <summary>
/// Thrown when a conversation export is asked for in a format this deployment has no renderer for:
/// disabled by configuration, or PDF with no usable font; answered with 503.
/// </summary>
/// <remarks>
/// The distinct problem type is what lets the client say "not available here" rather than "try again".
/// </remarks>
public class ExportRendererNotConfiguredException(ConversationExportFormats format)
    : ProblemException(
        HttpStatusCode.ServiceUnavailable,
        ProblemCodes.ExportRendererNotConfigured,
        $"Conversation export to {format.ToWireFormat()} is not available in this environment.")
{
    public string Format { get; } = format.ToWireFormat();

    /// <inheritdoc />
    /// <remarks>Lets a client offering several formats disable the one item rather than the whole control.</remarks>
    public override IReadOnlyDictionary<string, object?> Extensions =>
        new Dictionary<string, object?> { ["format"] = Format };
}
