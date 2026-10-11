using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Repository.Blob;
using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Service.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Testing;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Hosts the real API pipeline in the <c>Testing</c> environment (no startup migration or Cosmos DB
/// setup) against the Testcontainers database, with <see cref="TestAuthHandler"/> in place of Entra ID.
/// </summary>
public class CustomWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    private readonly string _connectionString = connectionString;

    public FakeGraphService GraphService { get; } = new();

    public FakeDocumentStore BlobStorage { get; } = new();

    public FakeEmbeddingGenerator EmbeddingGenerator { get; } = new();

    public FakeTranscriptStore Transcripts { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(HostEnvironments.Testing);

        // UseSetting, not ConfigureAppConfiguration: startup reads several of these inline, before
        // factory-added sources apply. No Azure endpoint is called; the clients only need well-formed values.
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:DefaultConnection"] = _connectionString,
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd:ClientSecret"] = "test-secret",
            ["AzureOpenAI:Url"] = "https://test.openai.azure.com/",
            ["AzureOpenAI:ApiKey"] = "test-key",
            ["AzureOpenAI:DefaultModel"] = "test-model",
            ["AzureOpenAI:EmbeddingModel"] = "test-embedding",
            // appsettings.json enables AzureAIFoundry with an empty Url, which fails options validation
            // at startup. No keyed client is also the shape the resolver's 503 path expects.
            ["AzureAIFoundry:Enabled"] = "false",
            // The well-known public emulator key; not a secret, and nothing connects to it under test.
            ["CosmosDb:ConnectionString"] =
                "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
            ["CosmosDb:DatabaseId"] = "test-db",
            ["CosmosDb:TranscriptContainerId"] = "test-transcript-container",
            ["AzureStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["AzureStorage:DocumentsContainer"] = "test-documents",
            ["AzureStorage:GeneratedContainer"] = "test-generated-documents",
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
            ["CorsOrigins:0"] = "https://localhost",
            // Startup requirements pinned so an appsettings.json edit cannot break the suite silently.
            // FileAgent:Enabled is deliberately absent: tests assert the value the repository ships.
            ["Summarization:ModelId"] = "c36e22ed-262a-47a1-b2ba-06a38355ae0f",
            ["FileAgent:ModelId"] = "e8b961c6-1fb6-4050-a265-dc72e5eab622"
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Registered after the application's own services, so these defaults win over the JWT bearer scheme.
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Only services that would make live network calls are substituted; extraction, chunking
            // and relational persistence run for real.
            services.RemoveAll<IGraphService>();
            services.AddSingleton<IGraphService>(GraphService);

            services.RemoveAll<IDocumentStore>();
            services.AddSingleton<IDocumentStore>(BlobStorage);

            services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(EmbeddingGenerator);

            // Without this, every conversation write blocks until the Cosmos SDK gives up on the
            // emulator address configured above and the endpoint answers 500 after ~25 seconds.
            services.RemoveAll<ITranscriptStore>();
            services.AddSingleton<ITranscriptStore>(Transcripts);

            // Scoped to the access-log category: an unfiltered collector would hold every record the
            // suite produces, EF Core included, for the shared fixture's lifetime.
            services.AddFakeLogging(options =>
                options.FilteredCategories = new HashSet<string> { typeof(RequestLoggingMiddleware).FullName! });
        });
    }

    public IReadOnlyList<FakeLogRecord> GetRequestLogs() =>
        Services.GetRequiredService<FakeLogCollector>().GetSnapshot();

    public void ClearRequestLogs() =>
        Services.GetRequiredService<FakeLogCollector>().Clear();

    public HttpClient CreateAdminClient()
    {
        return CreateClientForUser("admin");
    }

    public HttpClient CreateUserClient()
    {
        return CreateClientForUser("user");
    }

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
