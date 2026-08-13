using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Enterprise.Gpt.Service.Tokenization;

/// <summary>
/// One candidate message, reduced to what the budget needs to decide about it.
/// </summary>
/// <param name="Index">Its position in the candidate list.</param>
/// <param name="Role">Who produced it.</param>
/// <param name="Tokens">What it costs as context.</param>
public readonly record struct BudgetMessage(int Index, ChatRoles Role, long Tokens);

/// <summary>
/// What the budget decided about a candidate prompt.
/// </summary>
/// <param name="Kept">The messages that fit, in their original order.</param>
/// <param name="DroppedCount">How many messages were dropped.</param>
/// <param name="DroppedTokens">What those messages held.</param>
/// <param name="Budget">The prompt budget the decision was made against.</param>
/// <param name="IsUnbounded">Whether the model declares no context window, in which case nothing was dropped.</param>
/// <param name="ExceedsBudget">
/// Whether the undroppable messages alone exceed the budget. The caller fails the turn rather than
/// sending a prompt the model will reject.
/// </param>
public sealed record ContextBudgetResult(
    IReadOnlyList<BudgetMessage> Kept,
    int DroppedCount,
    long DroppedTokens,
    long Budget,
    bool IsUnbounded,
    bool ExceedsBudget);

/// <summary>
/// Decides which of a conversation's messages fit in the model's context window.
/// </summary>
public interface IContextBudgetCalculator
{
    /// <summary>
    /// Applies the model's context window to a candidate prompt.
    /// </summary>
    /// <param name="model">The model that will answer the turn.</param>
    /// <param name="messages">The candidate messages, oldest first, with the current prompt last.</param>
    /// <param name="overheadTokens">The tokens the prompt costs beyond its messages.</param>
    /// <returns>What fits, what did not, and why.</returns>
    ContextBudgetResult Apply(ModelDto model, IReadOnlyList<BudgetMessage> messages, long overheadTokens);
}

/// <summary>
/// Trims the oldest exchanges until the prompt fits, keeping the standing instructions and the
/// question being asked.
/// </summary>
/// <remarks>
/// Pure with respect to storage: it decides what is <em>sent</em>, never what is stored. The
/// transcript keeps every message whatever this returns.
/// </remarks>
/// <param name="logger">Receives the one-time notice for each model that declares no context window.</param>
public sealed class ContextBudgetCalculator(ILogger<ContextBudgetCalculator> logger) : IContextBudgetCalculator
{
    private readonly ILogger<ContextBudgetCalculator> _logger = logger;
    private readonly ConcurrentDictionary<Guid, byte> _warnedModels = new();

    /// <inheritdoc />
    public ContextBudgetResult Apply(ModelDto model, IReadOnlyList<BudgetMessage> messages, long overheadTokens)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(messages);

        if (model.ContextWindowSize <= 0)
        {
            // The seeded default for a catalog row nobody has filled in. Treating zero as a budget
            // would reject every turn on that model, so it means "unknown" rather than "none".
            if (_warnedModels.TryAdd(model.Id, 0))
            {
                _logger.LogWarning(
                    "Model {DeploymentName} declares no context window, so its turns are not trimmed. Set ContextWindowSize on the catalog row to enable budgeting.",
                    model.DeploymentName);
            }

            return new ContextBudgetResult(messages, 0, 0, 0, IsUnbounded: true, ExceedsBudget: false);
        }

        var budget = (long)model.ContextWindowSize - (long)model.MaxOutputTokens - overheadTokens;
        var total = messages.Sum(message => message.Tokens);

        if (total <= budget)
        {
            return new ContextBudgetResult(messages, 0, 0, budget, IsUnbounded: false, ExceedsBudget: false);
        }

        // The standing instructions and the question being asked are what the turn is for; dropping
        // either answers a different question than the user asked.
        var firstDroppable = messages.Count > 0 && messages[0].Role == ChatRoles.System ? 1 : 0;
        var lastDroppable = messages.Count - 1;

        var dropped = new HashSet<int>();
        var remaining = total;
        var index = firstDroppable;

        while (remaining > budget && index < lastDroppable)
        {
            dropped.Add(index);
            remaining -= messages[index].Tokens;

            // A user message and the assistant's reply to it leave together: a reply left behind
            // shows the model an answer to a question it can no longer see.
            if (messages[index].Role == ChatRoles.User
                && index + 1 < lastDroppable
                && messages[index + 1].Role == ChatRoles.Assistant)
            {
                index++;
                dropped.Add(index);
                remaining -= messages[index].Tokens;
            }

            index++;
        }

        List<BudgetMessage> kept = [.. messages.Where((_, position) => !dropped.Contains(position))];
        var droppedTokens = total - kept.Sum(message => message.Tokens);

        return new ContextBudgetResult(
            kept,
            dropped.Count,
            droppedTokens,
            budget,
            IsUnbounded: false,
            ExceedsBudget: remaining > budget);
    }

}
