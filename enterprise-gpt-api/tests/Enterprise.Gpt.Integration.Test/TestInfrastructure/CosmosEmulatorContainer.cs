using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Owns the Linux Cosmos DB emulator container the transcript tests run against.
/// </summary>
/// <remarks>
/// Built from the generic container builder rather than the <c>Testcontainers.CosmosDb</c> module,
/// which targets the older Windows-based emulator image. Hierarchical partition keys, transactional
/// batches and ordered cross-document queries are precisely what an in-memory fake cannot prove, so
/// the storage layer is exercised against a real engine.
/// </remarks>
public sealed class CosmosEmulatorContainer : IAsyncDisposable
{
    /// <summary>
    /// The account key every Cosmos emulator accepts. Published in Microsoft's documentation and
    /// not a secret.
    /// </summary>
    public const string EmulatorAccountKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string Image = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest";
    private const int GatewayPort = 8081;
    private const int HealthPort = 8080;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(Image)
        // The .NET SDK refuses plain HTTP against the emulator, and the vNext image serves HTTP by
        // default, so the protocol has to be switched explicitly.
        .WithCommand("--protocol", "https")
        .WithPortBinding(GatewayPort, assignRandomHostPort: true)
        .WithPortBinding(HealthPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPort(HealthPort).ForPath("/ready")))
        .Build();

    /// <summary>
    /// Gets the connection string addressing the running emulator.
    /// </summary>
    public string ConnectionString =>
        $"AccountEndpoint=https://{_container.Hostname}:{_container.GetMappedPublicPort(GatewayPort)}/;AccountKey={EmulatorAccountKey}";

    /// <summary>
    /// Starts the emulator, failing with a message naming it rather than timing out silently.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>A task that represents the asynchronous start operation.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the emulator cannot be started, so a missing Docker daemon or an unavailable
    /// image is reported as itself instead of as a connection failure inside a test.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _container.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start the Cosmos DB emulator container ('{Image}'). Integration tests need a running "
                + "Docker daemon and this image pulled. Run `dotnet test --filter \"Category!=Integration\"` to skip them.",
                ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
