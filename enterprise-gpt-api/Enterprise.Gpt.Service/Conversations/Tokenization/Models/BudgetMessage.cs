using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Conversations.Tokenization.Models;

/// <summary>
/// One message as the budget calculator sees it.
/// </summary>
public readonly record struct BudgetMessage(ChatRoles Role, long Tokens);
