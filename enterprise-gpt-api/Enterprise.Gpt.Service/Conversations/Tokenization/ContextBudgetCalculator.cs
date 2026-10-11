using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Conversations.Tokenization.Models;

namespace Enterprise.Gpt.Service.Conversations.Tokenization;

/// <summary>
/// Decides which of a conversation's messages fit the model that is about to answer.
/// </summary>
/// <remarks>
/// Deliberately pure so trimming, the decision most likely to fail unnoticed, is testable without a
/// running turn. It decides what is <em>sent</em>, never what is <em>stored</em>.
/// </remarks>
public static class ContextBudgetCalculator
{
    #region Public static methods

    /// <summary>
    /// Applies a model's context window to a candidate message list.
    /// </summary>
    /// <param name="messages">Chronological, with the current prompt last.</param>
    /// <param name="contextWindowSize">The model's total context window, in tokens.</param>
    /// <param name="maxOutputTokens">The tokens the model reserves for its answer.</param>
    /// <param name="turnOverhead">Tokens belonging to no message (schemas, instructions, framing).</param>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    /// <remarks>
    /// A zero <paramref name="contextWindowSize"/> (an unset catalog row) means unbounded: trimming
    /// everything is worse than a prompt that might be rejected. System and last messages are never dropped.
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

        // Never dropped: the standing instructions and the question this turn answers.
        var protectedIndexes = new HashSet<int>(
            messages.Select((message, index) => (message, index))
                .Where(x => x.message.Role is ChatRoles.System)
                .Select(x => x.index));

        if (messages.Count > 0)
        {
            protectedIndexes.Add(messages.Count - 1);
        }

        var kept = new HashSet<int>(all);
        var remaining = total;

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

    #endregion
}
