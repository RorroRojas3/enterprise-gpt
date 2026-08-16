using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Service.Serialization;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Transcripts;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Runs the Azure Cosmos DB Linux emulator in Docker and exposes a real
/// <see cref="ITranscriptStore"/> over it.
/// </summary>
/// <remarks>
/// <para>
/// Transactional batches and ordered cross-document queries are precisely what an in-memory fake
/// cannot prove — a fake that interpreted JSON-patch semantics would be asserting its own
/// arrangement — so the storage layer is proved here or not at all.
/// </para>
/// <para>
/// Its own collection rather than the shared SQL Server one, so the emulator starts only when these
/// tests actually run. Every test class still carries the integration trait, so
/// <c>dotnet test --filter "Category!=Integration"</c> needs no Docker.
/// </para>
/// </remarks>
public sealed class CosmosEmulatorFixture : IAsyncLifetime
{
    /// <summary>
    /// The emulator's well-known account key, published in Microsoft's documentation. Not a secret,
    /// and it reaches nothing outside the container.
    /// </summary>
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const ushort GatewayPort = 8081;
    private const ushort HealthPort = 8080;

    // The vNext emulator rather than the legacy image: it starts in seconds instead of minutes and
    // runs on any processor architecture. It serves HTTP by default and the .NET SDK does not
    // support that, hence the explicit protocol below.
    private readonly IContainer _container =
        new ContainerBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest")
        .WithPortBinding(GatewayPort, true)
        .WithPortBinding(HealthPort, true)
        .WithCommand("--protocol", "https")
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(HealthPort).ForPath("/ready")))
        .Build();

    private CosmosClient? _client;

    /// <summary>
    /// Gets the store under test, reading and writing the provisioned transcript container.
    /// </summary>
    public ITranscriptStore Store { get; private set; } = null!;

    /// <summary>
    /// Gets the transcript container, for assertions the store's own surface does not expose.
    /// </summary>
    public Container Container { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The Azure Cosmos DB emulator container could not be started. These tests need Docker and the image mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest; run 'dotnet test --filter \"Category!=Integration\"' to skip them.", ex);
        }

        var options = new CosmosOptions
        {
            ConnectionString = $"AccountEndpoint=https://localhost:{_container.GetMappedPublicPort(GatewayPort)}/;AccountKey={EmulatorKey};",
            DatabaseId = "transcript-tests",
            TranscriptContainerId = "transcript",
            PageSize = 100
        };

        _client = new CosmosClient(options.ConnectionString, new CosmosClientOptions
        {
            // The emulator answers only over the gateway, and it presents a self-signed certificate
            // that no machine store trusts. Both settings are scoped to this test client.
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }),
            UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateOptions()
        });

        // The production bootstrap, so the container these tests run against is provisioned exactly
        // as a deployment's is.
        await CosmosBootstrapper.EnsureProvisionedAsync(
            _client, options, NullLogger.Instance, CancellationToken.None);

        Container = _client.GetContainer(options.DatabaseId, options.TranscriptContainerId);
        Store = new TranscriptStore(_client, Options.Create(options), NullLogger<TranscriptStore>.Instance);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync();
    }
}

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
