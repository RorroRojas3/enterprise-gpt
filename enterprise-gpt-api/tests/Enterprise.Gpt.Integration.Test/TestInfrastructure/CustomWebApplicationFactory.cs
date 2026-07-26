using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Hosts the real API pipeline for integration tests. Runs in the <c>Testing</c> environment
/// (which skips the startup migration and Cosmos DB setup in <c>Program.cs</c>), points the
/// DbContext at the Testcontainers SQL Server database, supplies well-formed fake configuration
/// for the eagerly constructed Azure clients, and swaps the Entra ID JWT scheme for
/// <see cref="TestAuthHandler"/>.
/// </summary>
/// <param name="connectionString">Connection string of the containerized test database.</param>
public class CustomWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    private readonly string _connectionString = connectionString;

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
            // The AccountKey is the well-known public Cosmos DB emulator key, published in
            // Microsoft's docs — not a secret, and nothing ever connects to it under test.
            ["CosmosDb:ConnectionString"] =
                "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
            ["CosmosDb:DatabaseId"] = "test-db",
            ["CosmosDb:ContainerId"] = "test-container",
            ["AzureStorage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["DocumentIntelligence:Endpoint"] = "https://localhost/",
            ["DocumentIntelligence:ApiKey"] = "test-key",
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
        });
    }

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
