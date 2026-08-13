using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Testing;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Service;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Hosts the real API pipeline for integration tests. Runs in the <c>Testing</c> environment
/// (which skips the startup migration and Cosmos DB setup in <c>Program.cs</c>), points the
/// DbContext at the Testcontainers SQL Server database, supplies well-formed fake configuration
/// for the eagerly constructed Azure clients, and swaps the Entra ID JWT scheme for
/// <see cref="TestAuthHandler"/>.
/// </summary>
/// <param name="connectionString">Connection string of the containerized test database.</param>
/// <param name="cosmosConnectionString">Connection string of the containerized Cosmos emulator.</param>
public class CustomWebApplicationFactory(string connectionString, string cosmosConnectionString) : WebApplicationFactory<Program>
{
    private readonly string _connectionString = connectionString;
    private readonly string _cosmosConnectionString = cosmosConnectionString;

    /// <summary>
    /// Gets the in-memory directory backing <see cref="IGraphService"/> for the whole run. Tests
    /// register the directory users their scenario needs and reset it for isolation.
    /// </summary>
    public FakeGraphService GraphService { get; } = new();

    /// <summary>
    /// Gets the in-memory blob store backing <see cref="IBlobStorageService"/> for the whole run, so
    /// upload tests can assert on the stored path without an Azure Storage account.
    /// </summary>
    public FakeBlobStorageService BlobStorage { get; } = new();

    /// <summary>
    /// Gets the stub embedding generator, so upload tests can assert that chunks are embedded in batches.
    /// </summary>
    public FakeEmbeddingGenerator EmbeddingGenerator { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting (not ConfigureAppConfiguration) so the values reach the
        // WebApplicationBuilder's initial configuration — Program.cs reads several of these
        // inline during startup, before factory-added configuration sources would apply.
        // None of the Azure endpoints are ever called: the clients only need well-formed
        // URIs/keys to construct, and the Model endpoints never resolve them.
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:DefaultConnection"] = _connectionString,
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd:ClientSecret"] = "test-secret",
            ["AzureAIFoundry:Url"] = "https://test.openai.azure.com/",
            ["AzureAIFoundry:ApiKey"] = "test-key",
            ["AzureAIFoundry:DefaultModel"] = "test-model",
            ["AzureAIFoundry:EmbeddingModel"] = "test-embedding",
            // Points at the Testcontainers emulator, which these tests really do connect to: the
            // storage layer's whole point is behaviour a fake cannot reproduce.
            ["CosmosDb:ConnectionString"] = _cosmosConnectionString,
            ["CosmosDb:DatabaseId"] = "test-db",
            // Deliberately not CosmosDb:ContainerId — options validation rejects that retired key,
            // so leaving it here would make every integration test fail at host startup, which is
            // exactly the signal a deployment that has not cut over should get.
            ["CosmosDb:TranscriptContainerId"] = "test-transcripts",
            ["AzureStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["AzureStorage:DocumentsContainer"] = "test-documents",
            ["DocumentIntelligence:Endpoint"] = "https://localhost/",
            ["DocumentIntelligence:ApiKey"] = "test-key",
            ["DocumentIntelligence:OCRModelId"] = "prebuilt-read",
            // Small enough that an oversize upload can be arranged without allocating 50 MB.
            ["Documents:MaxFileSizeBytes"] = "65536",
            ["Documents:MaxQueuedBytes"] = "1048576",
            // Small so a modest fixture still spans several chunks and several embedding batches.
            ["Documents:EmbeddingBatchSize"] = "2",
            ["Documents:Chunking:MaxTokens"] = "64",
            ["Documents:Chunking:OverlapTokens"] = "16",
            ["CorsOrigins:0"] = "https://localhost"
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Registered after Program.cs, so these defaults win over the JWT bearer scheme.
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Only the services that would otherwise make live network calls against the fake
            // credentials above are substituted: Microsoft Graph for user provisioning, and blob
            // storage plus the embedding generator for document ingestion. Extraction, chunking,
            // relational persistence and the Cosmos transcript all run for real.
            services.RemoveAll<IGraphService>();
            services.AddSingleton<IGraphService>(GraphService);

            services.RemoveAll<IBlobStorageService>();
            services.AddSingleton<IBlobStorageService>(BlobStorage);

            services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(EmbeddingGenerator);

            // The emulator serves a self-signed certificate on a mapped port, so the client needs
            // gateway mode, an endpoint it will not try to look past, and a validation callback
            // that accepts the certificate. Everything else — the serializer above all — matches
            // what Program.cs configures, so the tests exercise the real serialization contract.
            services.RemoveAll<CosmosClient>();
            services.AddSingleton(new CosmosClient(_cosmosConnectionString, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                ServerCertificateCustomValidationCallback = (_, _, _) => true,
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.Options
            }));

            // Scoped to the access-log category on purpose: an unfiltered collector accumulates every
            // record the whole suite produces — EF Core included — for the lifetime of the shared
            // fixture, which is a slow memory leak in exchange for records no test reads.
            services.AddFakeLogging(options =>
                options.FilteredCategories = new HashSet<string> { typeof(RequestLoggingMiddleware).FullName! });
        });
    }

    /// <summary>
    /// Gets the access-log records written since the last reset.
    /// </summary>
    /// <returns>A snapshot of the collected records.</returns>
    public IReadOnlyList<FakeLogRecord> GetRequestLogs() =>
        Services.GetRequiredService<FakeLogCollector>().GetSnapshot();

    /// <summary>
    /// Discards the access-log records collected so far, so a test sees only its own.
    /// </summary>
    public void ClearRequestLogs() =>
        Services.GetRequiredService<FakeLogCollector>().Clear();

    /// <summary>
    /// Creates a client authenticated as the seeded administrator.
    /// </summary>
    /// <returns>An <see cref="HttpClient"/> carrying the admin test-user header.</returns>
    public HttpClient CreateAdminClient()
    {
        return CreateClientForUser("admin");
    }

    /// <summary>
    /// Creates a client authenticated as the seeded non-admin user.
    /// </summary>
    /// <returns>An <see cref="HttpClient"/> carrying the regular test-user header.</returns>
    public HttpClient CreateUserClient()
    {
        return CreateClientForUser("user");
    }

    /// <summary>
    /// Creates a client with no identity, so protected endpoints respond 401.
    /// </summary>
    /// <returns>An unauthenticated <see cref="HttpClient"/>.</returns>
    public HttpClient CreateAnonymousClient()
    {
        return CreateClient();
    }

    private HttpClient CreateClientForUser(string user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }
}
