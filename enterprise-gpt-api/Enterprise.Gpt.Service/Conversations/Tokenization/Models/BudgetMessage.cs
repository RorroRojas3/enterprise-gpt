using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Conversations.Tokenization.Models;

/// <summary>
/// One message as the budget calculator sees it.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Tokens">What it contributes to the prompt.</param>
public readonly record struct BudgetMessage(ChatRoles Role, long Tokens);
