using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Tokenization;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tokenization;

/// <summary>
/// The calculator runs with no <c>DbContext</c>, no chat client and no Cosmos dependency, which is
/// the point: trimming is the decision most likely to be wrong in a way nobody notices — a
/// conversation quietly losing its early context — so it must be testable without a running turn.
/// </summary>
public sealed class ContextBudgetCalculatorTests
{
    private static BudgetMessage System(long tokens) => new(ChatRoles.System, tokens);

    private static BudgetMessage User(long tokens) => new(ChatRoles.User, tokens);

    private static BudgetMessage Assistant(long tokens) => new(ChatRoles.Assistant, tokens);

    [Fact]
    public void Apply_ListThatFits_KeepsEverythingAndReportsNothingDropped()
    {
        BudgetMessage[] messages = [System(10), User(20), Assistant(30), User(5)];

        var result = ContextBudgetCalculator.Apply(messages, 128000m, 4096m, turnOverhead: 100);

        Assert.Equal([0, 1, 2, 3], result.KeptIndexes);
        Assert.Equal(0, result.DroppedMessages);
        Assert.Equal(0, result.DroppedTokens);
        Assert.True(result.Fits);
        Assert.False(result.IsUnbounded);
    }

    [Fact]
    public void Apply_ListThatOverflows_DropsTheOldestUntilItFits()
    {
        // Budget = 1000 - 100 (output) - 0 (overhead) = 900. Total is 1200, so the oldest droppable
        // pair has to go.
        BudgetMessage[] messages = [System(50), User(200), Assistant(150), User(500), Assistant(200), User(100)];

        var result = ContextBudgetCalculator.Apply(messages, 1000m, 100m, turnOverhead: 0);

        Assert.True(result.Fits);
        Assert.True(result.DroppedMessages > 0);
        Assert.Equal(350, result.DroppedTokens);
        Assert.DoesNotContain(1, result.KeptIndexes);
        Assert.DoesNotContain(2, result.KeptIndexes);
    }

    [Fact]
    public void Apply_Trimming_NeverDropsTheSystemMessage()
    {
        BudgetMessage[] messages = [System(50), User(500), Assistant(500), User(50)];

        var result = ContextBudgetCalculator.Apply(messages, 200m, 50m, turnOverhead: 0);

        Assert.Contains(0, result.KeptIndexes);
    }

    [Fact]
    public void Apply_Trimming_NeverDropsTheCurrentPrompt()
    {
        BudgetMessage[] messages = [System(50), User(500), Assistant(500), User(50)];

        var result = ContextBudgetCalculator.Apply(messages, 200m, 50m, turnOverhead: 0);

        Assert.Contains(messages.Length - 1, result.KeptIndexes);
    }

    /// <summary>
    /// The model must never be shown an answer to a question it can no longer see.
    /// </summary>
    [Fact]
    public void Apply_DroppingAUserMessage_DropsTheAssistantMessageThatAnsweredIt()
    {
        BudgetMessage[] messages = [System(10), User(400), Assistant(400), User(10), Assistant(10), User(10)];

        var result = ContextBudgetCalculator.Apply(messages, 500m, 50m, turnOverhead: 0);

        Assert.DoesNotContain(1, result.KeptIndexes);
        Assert.DoesNotContain(2, result.KeptIndexes);
    }

    /// <summary>
    /// The seeded default for a catalog row nobody has filled in. Treating it as a budget of zero
    /// would trim every conversation to nothing over an unset field.
    /// </summary>
    [Fact]
    public void Apply_ContextWindowOfZero_TreatsTheModelAsUnboundedAndDropsNothing()
    {
        BudgetMessage[] messages = [System(1000), User(50000), Assistant(50000), User(10)];

        var result = ContextBudgetCalculator.Apply(messages, 0m, 0m, turnOverhead: 0);

        Assert.True(result.IsUnbounded);
        Assert.Equal(0, result.DroppedMessages);
        Assert.Equal([0, 1, 2, 3], result.KeptIndexes);
        Assert.True(result.Fits);
    }

    /// <summary>
    /// When what cannot be dropped exceeds the window on its own, the turn has to fail rather than
    /// be sent: the provider would reject it after the user had already waited.
    /// </summary>
    [Fact]
    public void Apply_SystemMessageAndPromptAloneExceedTheBudget_ReportsItDoesNotFit()
    {
        BudgetMessage[] messages = [System(5000), User(20), Assistant(20), User(5000)];

        var result = ContextBudgetCalculator.Apply(messages, 1000m, 100m, turnOverhead: 0);

        Assert.False(result.Fits);
    }

    [Fact]
    public void Apply_TurnOverhead_ReducesTheAvailableBudget()
    {
        BudgetMessage[] messages = [System(10), User(400), Assistant(400), User(10)];

        var withoutOverhead = ContextBudgetCalculator.Apply(messages, 1000m, 100m, turnOverhead: 0);
        var withOverhead = ContextBudgetCalculator.Apply(messages, 1000m, 100m, turnOverhead: 500);

        Assert.Equal(0, withoutOverhead.DroppedMessages);
        Assert.True(withOverhead.DroppedMessages > 0);
    }

    [Fact]
    public void Apply_KeptIndexes_StayInChronologicalOrder()
    {
        BudgetMessage[] messages = [System(10), User(400), Assistant(400), User(10), Assistant(10), User(10)];

        var result = ContextBudgetCalculator.Apply(messages, 500m, 50m, turnOverhead: 0);

        Assert.Equal([.. result.KeptIndexes.Order()], result.KeptIndexes);
    }

    [Fact]
    public void Apply_EmptyList_ReturnsEmpty()
    {
        var result = ContextBudgetCalculator.Apply([], 1000m, 100m, turnOverhead: 0);

        Assert.Empty(result.KeptIndexes);
        Assert.True(result.Fits);
    }

    [Fact]
    public void Apply_NullMessages_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ContextBudgetCalculator.Apply(null!, 1000m, 100m, 0));
    }
}
