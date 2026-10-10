using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure.FileAgentSpike;

/// <summary>
/// Serializes every sandbox probe onto one session.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SandboxSpikeCollection : ICollectionFixture<SandboxSpikeFixture>
{
    public const string Name = "FileAgentSpike";
}
