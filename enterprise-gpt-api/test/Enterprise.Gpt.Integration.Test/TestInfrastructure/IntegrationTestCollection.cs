using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Serializes all integration test classes onto one shared container and host, so tests can
/// safely mutate the shared database.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
    /// <summary>
    /// The collection name integration test classes join via <c>[Collection]</c>.
    /// </summary>
    public const string Name = "Integration";
}
