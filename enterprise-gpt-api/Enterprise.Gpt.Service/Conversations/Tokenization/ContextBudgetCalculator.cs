using Enterprise.Gpt.Common.Enums;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// One message as the budget calculator sees it.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Tokens">What it contributes to the prompt.</param>
public readonly record struct BudgetMessage(ChatRoles Role, long Tokens);

/// <summary>
/// What a turn's history looked like after the budget was applied.
/// </summary>
/// <param name="KeptIndexes">
/// The positions, in the original list, of the messages that fit — in their original order.
/// </param>
/// <param name="DroppedMessages">How many messages were dropped.</param>
/// <param name="DroppedTokens">How many tokens those messages held.</param>
/// <param name="IsUnbounded">Whether the model declared no context window, so nothing was measured.</param>
/// <param name="Fits">
/// Whether the result fits the budget. False only when what cannot be dropped — the system message
/// and the current prompt — exceeds it on its own.
/// </param>
public sealed record ContextBudgetResult(
    IReadOnlyList<int> KeptIndexes,
    int DroppedMessages,
    long DroppedTokens,
    bool IsUnbounded,
    bool Fits);

/// <summary>
/// Decides which of a conversation's messages fit the model that is about to answer.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately pure: no database, no chat client, no Cosmos. Trimming is the decision most likely
/// to be wrong in a way nobody notices — a conversation quietly losing its early context — so it is
/// the decision that most needs to be testable without a running turn.
/// </para>
/// <para>
/// It decides what is <em>sent</em>, never what is <em>stored</em>. The transcript keeps every
/// message regardless.
/// </para>
/// </remarks>
public static class ContextBudgetCalculator
{
    /// <summary>
    /// Applies a model's context window to a candidate message list.
    /// </summary>
    /// <param name="messages">
    /// The messages in chronological order, with the current prompt last.
    /// </param>
    /// <param name="contextWindowSize">The model's total context window, in tokens.</param>
    /// <param name="maxOutputTokens">The tokens the model reserves for its answer.</param>
    /// <param name="turnOverhead">
    /// The prompt tokens belonging to no message — tool schemas, instructions and framing.
    /// </param>
    /// <returns>Which messages fit, and what dropping the rest cost.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="messages"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A <paramref name="contextWindowSize"/> of zero is the seeded default for a catalog row nobody
    /// has filled in. It is treated as unbounded rather than as a budget of zero, because trimming
    /// every conversation to nothing over an unset catalog field would be a far worse failure than
    /// sending a prompt that might be rejected.
    /// </para>
    /// <para>
    /// The system message and the last message are never dropped: the first is the assistant's
    /// standing instructions, and the second is the question being asked. When a user message is
    /// dropped the assistant message answering it goes too, so the model never sees an answer to a
    /// question that is no longer there.
    /// </para>
    /// </remarks>
    public static ContextBudgetResult Apply(
        IReadOnlyList<BudgetMessage> messages,
        decimal contextWindowSize,
        decimal maxOutputTokens,
        long turnOverhead)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var all = Enumerable.Range(0, messages.Count).ToList();

        if (contextWindowSize <= 0)
        {
            return new ContextBudgetResult(all, 0, 0, IsUnbounded: true, Fits: true);
        }

        var budget = (long)decimal.Truncate(contextWindowSize - maxOutputTokens) - turnOverhead;
        var total = messages.Sum(x => x.Tokens);

        if (total <= budget)
        {
            return new ContextBudgetResult(all, 0, 0, IsUnbounded: false, Fits: true);
        }

        // Protected regardless of budget: the system message carries the assistant's standing
        // instructions, and the final message is the question this turn exists to answer.
        var protectedIndexes = new HashSet<int>(
            messages.Select((message, index) => (message, index))
                .Where(x => x.message.Role is ChatRoles.System)
                .Select(x => x.index));

        if (messages.Count > 0)
        {
            protectedIndexes.Add(messages.Count - 1);
        }

        var kept = new HashSet<int>(all);
        long remaining = total;

        // Oldest first, so what is lost is always the least recent context.
        for (var index = 0; index < messages.Count && remaining > budget; index++)
        {
            if (protectedIndexes.Contains(index) || !kept.Contains(index))
            {
                continue;
            }

            kept.Remove(index);
            remaining -= messages[index].Tokens;

            // A dropped user message takes the assistant message that answered it, so the model is
            // never shown a reply to a question it can no longer see.
            if (messages[index].Role is ChatRoles.User)
            {
                for (var next = index + 1; next < messages.Count; next++)
                {
                    if (messages[next].Role is ChatRoles.User)
                    {
                        break;
                    }

                    if (messages[next].Role is ChatRoles.Assistant
                        && !protectedIndexes.Contains(next)
                        && kept.Remove(next))
                    {
                        remaining -= messages[next].Tokens;
                        break;
                    }
                }
            }
        }

        var keptIndexes = all.Where(kept.Contains).ToList();

        return new ContextBudgetResult(
            keptIndexes,
            messages.Count - keptIndexes.Count,
            total - remaining,
            IsUnbounded: false,
            Fits: remaining <= budget);
    }
}
