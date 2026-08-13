using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Service.Tokenization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tokenization;

/// <summary>
/// Runs with no database, no chat client and no Cosmos: the trimming decision is arithmetic over
/// counts, and keeping it that way is what makes these cases cheap to state.
/// </summary>
public class ContextBudgetCalculatorTests
{
    private readonly ContextBudgetCalculator _calculator = new(NullLogger<ContextBudgetCalculator>.Instance);

    private static ModelDto CreateModel(decimal contextWindowSize = 128_000m, decimal maxOutputTokens = 4_096m) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = Guid.NewGuid(),
        Name = "GPT Test",
        DeploymentName = "gpt-test",
        Description = "A test model.",
        ContextWindowSize = contextWindowSize,
        MaxOutputTokens = maxOutputTokens
    };

    private static List<BudgetMessage> BuildConversation(params (ChatRoles Role, long Tokens)[] messages) =>
        [.. messages.Select((message, index) => new BudgetMessage(index, message.Role, message.Tokens))];

    [Fact]
    public void Apply_WindowAndOutputAndOverhead_LeavesTheRemainderAsTheBudget()
    {
        var result = _calculator.Apply(CreateModel(), BuildConversation((ChatRoles.User, 10)), overheadTokens: 500);

        Assert.Equal(123_404, result.Budget);
    }

    [Fact]
    public void Apply_PromptThatAlreadyFits_ReturnsEveryMessageAndDropsNothing()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 50),
            (ChatRoles.User, 20),
            (ChatRoles.Assistant, 30),
            (ChatRoles.User, 10));

        var result = _calculator.Apply(CreateModel(), messages, overheadTokens: 0);

        Assert.Equal(messages.Count, result.Kept.Count);
        Assert.Equal(0, result.DroppedCount);
        Assert.Equal(0, result.DroppedTokens);
        Assert.False(result.ExceedsBudget);
    }

    /// <summary>
    /// The standing instructions and the question being asked are what the turn is for, so they
    /// survive any amount of trimming.
    /// </summary>
    [Fact]
    public void Apply_OverBudget_KeepsTheSystemMessageFirstAndThePromptLast()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 100),
            (ChatRoles.User, 400),
            (ChatRoles.Assistant, 400),
            (ChatRoles.User, 400),
            (ChatRoles.Assistant, 400),
            (ChatRoles.User, 100));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 1_000m, maxOutputTokens: 100m), messages, overheadTokens: 0);

        Assert.Equal(ChatRoles.System, result.Kept[0].Role);
        Assert.Equal(ChatRoles.User, result.Kept[^1].Role);
        Assert.Equal(100, result.Kept[^1].Tokens);
        Assert.True(result.DroppedCount > 0);
    }

    /// <summary>
    /// A reply left behind without its question shows the model an answer to something it can no
    /// longer see, so the pair leaves together.
    /// </summary>
    [Fact]
    public void Apply_DroppingAUserMessage_DropsTheAssistantReplyWithIt()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 10),
            (ChatRoles.User, 300),
            (ChatRoles.Assistant, 10),
            (ChatRoles.User, 10));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 200m, maxOutputTokens: 50m), messages, overheadTokens: 0);

        Assert.DoesNotContain(result.Kept, message => message.Role == ChatRoles.Assistant);
    }

    [Fact]
    public void Apply_OverBudget_ReportsWhatItDropped()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 10),
            (ChatRoles.User, 500),
            (ChatRoles.Assistant, 500),
            (ChatRoles.User, 10));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 200m, maxOutputTokens: 50m), messages, overheadTokens: 0);

        Assert.Equal(2, result.DroppedCount);
        Assert.Equal(1_000, result.DroppedTokens);
    }

    /// <summary>
    /// Zero is the seeded default for a catalog row nobody has filled in, so it means "unknown"
    /// rather than "no room" — budgeting on it would reject every turn on that model.
    /// </summary>
    [Fact]
    public void Apply_ModelWithNoContextWindow_TreatsItAsUnboundedAndDropsNothing()
    {
        var messages = BuildConversation((ChatRoles.System, 10), (ChatRoles.User, 1_000_000));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 0m, maxOutputTokens: 0m), messages, overheadTokens: 0);

        Assert.True(result.IsUnbounded);
        Assert.Equal(messages.Count, result.Kept.Count);
        Assert.Equal(0, result.DroppedCount);
    }

    /// <summary>
    /// When the undroppable messages alone overflow, no trimming can make the turn sendable — the
    /// caller has to fail it rather than send a prompt the model will reject.
    /// </summary>
    [Fact]
    public void Apply_SystemAndPromptAloneExceedTheBudget_ReportsItRatherThanTrimmingThemAway()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 5_000),
            (ChatRoles.User, 5_000));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 1_000m, maxOutputTokens: 100m), messages, overheadTokens: 0);

        Assert.True(result.ExceedsBudget);
        Assert.Contains(result.Kept, message => message.Role == ChatRoles.System);
        Assert.Contains(result.Kept, message => message.Role == ChatRoles.User);
    }

    [Fact]
    public void Apply_ConversationWithoutASystemMessage_StillKeepsThePrompt()
    {
        var messages = BuildConversation(
            (ChatRoles.User, 500),
            (ChatRoles.Assistant, 500),
            (ChatRoles.User, 10));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 200m, maxOutputTokens: 50m), messages, overheadTokens: 0);

        Assert.Equal(ChatRoles.User, result.Kept[^1].Role);
        Assert.Equal(10, result.Kept[^1].Tokens);
    }

    [Fact]
    public void Apply_KeptMessages_StayInTheirOriginalOrder()
    {
        var messages = BuildConversation(
            (ChatRoles.System, 10),
            (ChatRoles.User, 900),
            (ChatRoles.Assistant, 10),
            (ChatRoles.User, 10),
            (ChatRoles.Assistant, 10),
            (ChatRoles.User, 10));

        var result = _calculator.Apply(CreateModel(contextWindowSize: 200m, maxOutputTokens: 50m), messages, overheadTokens: 0);

        var indexes = result.Kept.Select(message => message.Index).ToList();
        Assert.Equal(indexes.OrderBy(index => index), indexes);
    }
}
