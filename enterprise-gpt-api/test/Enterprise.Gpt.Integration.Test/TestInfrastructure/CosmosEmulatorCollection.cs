using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Repository.Cosmos.Options;
using Enterprise.Gpt.Repository.Cosmos.Serialization;
using Enterprise.Gpt.Repository.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Serializes the emulator-backed test classes onto one container.
/// </summary>
[CollectionDefinition(Name)]
public class CosmosEmulatorCollection : ICollectionFixture<CosmosEmulatorFixture>
{
    /// <summary>
    /// The collection name the emulator-backed test classes join via <c>[Collection]</c>.
    /// </summary>
    public const string Name = "CosmosEmulator";
}
