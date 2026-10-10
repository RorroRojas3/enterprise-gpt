using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// One message of a conversation, reduced to what a rendered document needs.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Label">The heading an export writes above it — <c>You</c> or <c>Assistant</c>.</param>
/// <param name="Markdown">The message text as authored, which every rendered format is built from.</param>
/// <remarks>
/// The transcript's stored <c>htmlContent</c> is deliberately not carried. Rendering the HTML export
/// from it as well would have made one of the four formats disagree with the other three about what a
/// message contains — images in particular, which the block model drops.
/// </remarks>
public sealed record ConversationExportMessage(ChatRoles Role, string Label, string Markdown);
