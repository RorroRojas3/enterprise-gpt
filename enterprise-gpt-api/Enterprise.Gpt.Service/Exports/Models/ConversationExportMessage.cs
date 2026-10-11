using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Exports.Models;

/// <summary>
/// One message of a conversation, reduced to what a rendered document needs.
/// </summary>
/// <remarks>
/// The stored <c>htmlContent</c> is deliberately not carried, so every format renders from the same
/// markdown and agrees on content (images in particular, which the block model drops).
/// </remarks>
public sealed record ConversationExportMessage(ChatRoles Role, string Label, string Markdown);
