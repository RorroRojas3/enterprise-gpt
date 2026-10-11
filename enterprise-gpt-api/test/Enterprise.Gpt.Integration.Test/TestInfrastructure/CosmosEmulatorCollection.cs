using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Serializes the emulator-backed test classes onto one container.
/// </summary>
[CollectionDefinition(Name)]
public class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture>
{
    public const string Name = "CosmosEmulator";
}
