using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Agents;

/// <summary>
/// What the File Agent's own chat client is, as the container actually builds it.
/// </summary>
/// <remarks>
/// Asserted against the composed pipeline rather than against <c>Program.cs</c>'s source, because
/// both facts here are properties of a built client: they are settings on an instance, and reading
/// them any other way would prove only that the code says what it says.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FileAgentRegistrationTests(IntegrationTestFixture fixture)
{
    private readonly IntegrationTestFixture _fixture = fixture;

    [Fact]
    public void FileAgentClient_BoundsItsOwnToolLoop_RatherThanInheritingTheTurns()
    {
        // The agent spends iterations the turn does not — load a skill, read its reference, run the
        // code, re-open the artifact — and MaximumIterationsPerRequest is an instance setting with no
        // per-request override, so a shared client would silently cap it at the turn's own ceiling.
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
