using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Agents;

/// <summary>
/// Asserts the File Agent's chat client as the container builds it, since both facts are instance settings.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FileAgentRegistrationTests(IntegrationTestFixture fixture)
{
    private readonly IntegrationTestFixture _fixture = fixture;

    [Fact]
    public void FileAgentClient_BoundsItsOwnToolLoop_RatherThanInheritingTheTurns()
    {
        // The agent needs more iterations than a turn, and MaximumIterationsPerRequest has no
        // per-request override, so a shared client would silently cap it at the turn's ceiling.
        var turn = Invoker(ChatClientKeys.AzureOpenAI);
        var agent = Invoker(ChatClientKeys.FileAgent);

        Assert.Equal(5, turn.MaximumIterationsPerRequest);
        Assert.Equal(12, agent.MaximumIterationsPerRequest);
    }

    [Fact]
    public void FileAgentClient_InvokesOneToolAtATime()
    {
        // Load-bearing beyond the reason every other provider sets it: the agent mutates one shared
        // HostedCodeInterpreterTool's inputs between the calls of a single turn.
        Assert.False(Invoker(ChatClientKeys.FileAgent).AllowConcurrentInvocation);
        Assert.False(Invoker(ChatClientKeys.AzureOpenAI).AllowConcurrentInvocation);
    }

    private FunctionInvokingChatClient Invoker(string key)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredKeyedService<IChatClient>(key);

        return client.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException($"The '{key}' client has no function-invoking layer.");
    }
}
