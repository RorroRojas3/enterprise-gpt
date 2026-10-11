using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Serializes all integration test classes onto one shared container and host, so tests can
/// safely mutate the shared database.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration";
}
