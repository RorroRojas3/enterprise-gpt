using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Converters;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Settings;
using Scalar.AspNetCore;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        builder.Configuration.Bind("AzureAd", options);

        // Explicitly validate audience to ensure token is for this API
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudiences =
        [
            builder.Configuration["AzureAd:ClientId"],
            $"api://{builder.Configuration["AzureAd:ClientId"]}"
        ];
    },
    options => builder.Configuration.Bind("AzureAd", options))
    .EnableTokenAcquisitionToCallDownstreamApi(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
    })
    .AddInMemoryTokenCaches();

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.SuppressModelStateInvalidFilter = true;
    });

builder.Services.AddHttpContextAccessor();

builder.Services.AddDbContext<EnterpriseGptDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), sqlOptions =>
    {
        sqlOptions.UseCompatibilityLevel(170);
    }));

// OpenAPI document generation (served by Scalar in development).
builder.Services.AddOpenApi();

// Add CORS
var corsOrigins = builder.Configuration.GetSection("CorsOrigins").Get<string[]>();
corsOrigins ??= [];
builder.Services.AddCors(builder => builder.AddPolicy("AllowSpecificOrigins", policy =>
{
    policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("Content-Disposition");
}));


// Chat providers. Each is registered under the DI key its Core.Ref.Provider row maps to in
// Providers.ServiceKeys; IChatClientResolver is what turns a model's ProviderId into one of these at
// request time. Shared here so a second provider cannot drift from the first on tool invocation.
static void ConfigureFunctionInvocation(FunctionInvokingChatClient client)
{
    client.AllowConcurrentInvocation = false;
    client.IncludeDetailedErrors = true;
    client.MaximumIterationsPerRequest = 5;
    client.MaximumConsecutiveErrorsPerRequest = 5;
}

// 1) Azure AI Foundry under key "azureaifoundry"
var azureAIFoundryUrl = builder.Configuration.GetValue<string>("AzureAIFoundry:Url") ?? string.Empty;
var azureAIFoundryKey = builder.Configuration.GetValue<string>("AzureAIFoundry:ApiKey") ?? string.Empty;
var azureAIFoundryDefaultModel = builder.Configuration.GetValue<string>("AzureAIFoundry:DefaultModel") ?? string.Empty;
builder.Services.AddKeyedChatClient(
        ChatClientKeys.AzureAIFoundry,
        sp => new AzureOpenAIClient(new Uri(azureAIFoundryUrl), new ApiKeyCredential(azureAIFoundryKey))
                  .GetChatClient(azureAIFoundryDefaultModel)
                  .AsIChatClient()
    )
    .UseOpenTelemetry()
    .UseFunctionInvocation(null, ConfigureFunctionInvocation);

// 2) Amazon Bedrock under key "amazonbedrock", off unless AmazonBedrock:Enabled is set. Validation is
// gated on the same flag, so an environment that does not use Bedrock needs no Bedrock settings and a
// half-filled section still fails at startup rather than on the first conversation.
builder.Services.AddOptions<AmazonBedrockOptions>()
    .Bind(builder.Configuration.GetSection(AmazonBedrockOptions.SectionName))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "AmazonBedrock:ApiKey is required when AmazonBedrock:Enabled is true.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.DefaultModelId),
        "AmazonBedrock:DefaultModelId is required when AmazonBedrock:Enabled is true.")
    // Not GetBySystemName: it fabricates an endpoint for an unknown name instead of failing, which
    // would turn a typo into a DNS error on the first request. The list is baked into the pinned
    // AWSSDK.Core, so a region AWS launches after that pin is rejected here until the package is
    // bumped — the exception message is the only place that trade-off is visible.
    .Validate(options => !options.Enabled || RegionEndpoint.EnumerableAllRegions.Any(region => region.SystemName == options.Region),
        "AmazonBedrock:Region must be a known AWS region system name, for example us-east-1. Regions added to AWS after the pinned AWSSDK.Core version are not recognized until it is upgraded.")
    .ValidateOnStart();

if (builder.Configuration.GetValue<bool>($"{AmazonBedrockOptions.SectionName}:Enabled"))
{
    // Registered in its own right rather than constructed inside the chat-client factory: the MEAI
    // adapter's Dispose is a deliberate no-op because it does not own the runtime client, so a client
    // created inside that closure would be owned by nobody and never disposed.
    builder.Services.AddSingleton<IAmazonBedrockRuntime>(sp =>
    {
        var bedrockOptions = sp.GetRequiredService<IOptions<AmazonBedrockOptions>>().Value;
        var bedrockConfig = new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(bedrockOptions.Region),
            // The SDK's default token chain reads SSO profiles only — it never looks at
            // AWS_BEARER_TOKEN_BEDROCK — so a Bedrock API key has to be handed over here.
            // Pinning the preference is what stops SigV4, which the service's auth resolver
            // lists first, from winning on a host that carries ambient AWS credentials.
            AWSTokenProvider = new StaticTokenProvider(bedrockOptions.ApiKey, null),
            AuthSchemePreference = ["httpBearerAuth"]
        };

        // Anonymous credentials keep the SigV4 identity resolver from probing IMDS; the bearer
        // token is what authorizes the request.
        return new AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), bedrockConfig);
    });

    builder.Services.AddKeyedChatClient(
            ChatClientKeys.AmazonBedrock,
            sp => sp.GetRequiredService<IAmazonBedrockRuntime>()
                    .AsIChatClient(sp.GetRequiredService<IOptions<AmazonBedrockOptions>>().Value.DefaultModelId)
        )
        .UseOpenTelemetry()
        .UseFunctionInvocation(null, ConfigureFunctionInvocation);
}

builder.Services.AddSingleton<IChatClientResolver, ChatClientResolver>();

// AI Embedding Generators
var embeddingModel = builder.Configuration.GetValue<string>("AzureAIFoundry:EmbeddingModel") ?? string.Empty;
IEmbeddingGenerator<string, Embedding<float>> ollamaGenerator =
    new AzureOpenAIClient(new Uri(azureAIFoundryUrl), new ApiKeyCredential(azureAIFoundryKey))
        .GetEmbeddingClient(embeddingModel)
        .AsIEmbeddingGenerator();
builder.Services.AddEmbeddingGenerator(ollamaGenerator);

// Document ingestion options, validated at startup so a bad chunk size or size limit fails the app
// rather than every upload.
builder.Services.AddOptions<DocumentOptions>()
    .Bind(builder.Configuration.GetSection(DocumentOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.Chunking.OverlapTokens < options.Chunking.MaxTokens,
        "Documents:Chunking:OverlapTokens must be smaller than Documents:Chunking:MaxTokens.")
    .ValidateOnStart();

// Background job pipeline (replaces Hangfire). BackgroundJobProcessor consumes IBackgroundJobQueue,
// caps concurrent jobs via SemaphoreSlim, and updates IJobStatusStore for client polling.
builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddSingleton<IJobStatusStore, JobStatusStore>();
builder.Services.AddHostedService<BackgroundJobProcessor>();

// Add Microsoft Graph Service
builder.Services.AddSingleton(sp =>
{
    var tenantId = builder.Configuration["AzureAd:TenantId"];
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var clientSecret = builder.Configuration["AzureAd:ClientSecret"];

    var clientSecretCredential = new ClientSecretCredential(
        tenantId,
        clientId,
        clientSecret
    );

    return new GraphServiceClient(clientSecretCredential);
});

// Azure Storage
builder.Services.AddSingleton(x =>
{
    var connectionString = builder.Configuration["AzureStorage:ConnectionString"];
    return new BlobServiceClient(connectionString);
});

// Azure Document Intelligence 
builder.Services.AddSingleton(sp =>
{
    var config = builder.Configuration.GetSection("DocumentIntelligence");
    var endpoint = config["Endpoint"];
    var apiKey = config["ApiKey"];

    var credential = new AzureKeyCredential(apiKey!);
    return new DocumentIntelligenceClient(new Uri(endpoint!), credential);
});

// Register Azure Cosmos DB
var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
var cosmosDatabaseId = builder.Configuration["CosmosDb:DatabaseId"];
var cosmosContainerId = builder.Configuration["CosmosDb:ContainerId"];
var cosmosClientOptions = new CosmosClientOptions
{
    SerializerOptions = new CosmosSerializationOptions
    {
        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
    }
};
builder.Services.AddSingleton(sp => new CosmosClient(cosmosConnectionString, cosmosClientOptions));
builder.Services.AddScoped<IAzureCosmosService>(provider =>
{
    var cosmosClient = provider.GetRequiredService<CosmosClient>();
    return new AzureCosmosService(cosmosClient, cosmosDatabaseId!, cosmosContainerId!);
});

// MCP client/tool cache: singleton cache of live MCP connections, scoped provider that
// resolves permissions and acquires OBO tokens per request. The named HttpClient gets an
// infinite timeout because MCP sessions stream over SSE; connection establishment is
// bounded separately by McpClientCacheOptions.ConnectionTimeout.
builder.Services.Configure<McpClientCacheOptions>(builder.Configuration.GetSection("Mcp:Cache"));
builder.Services.AddSingleton<IMcpClientCache, McpClientCache>();
builder.Services.AddScoped<IMcpToolProvider, McpToolProvider>();
builder.Services.AddHttpClient(McpToolProvider.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

// Per-user permission cache: keyed by Entra object id, written at sign-in (POST api/users/me) and
// lazily on a filter cache miss, invalidated per user by the services that mutate grants. Entries
// also carry an absolute lifetime because invalidation is per-instance: in a scaled-out deployment
// a grant changed on another instance is only picked up when the entry expires. Validated at
// startup because a non-positive lifetime silently degrades the cache into a per-request query.
builder.Services.AddOptions<UserPermissionCacheOptions>()
    .Bind(builder.Configuration.GetSection(UserPermissionCacheOptions.SectionName))
    .Validate(options => options.EntryLifetime > TimeSpan.Zero,
        "Permissions:Cache:EntryLifetime must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton<IUserPermissionCache, UserPermissionCache>();

// Exception handlers (chained — first to return true wins; GlobalExceptionHandler is the fallback)
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<OperationCanceledExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// The write path for every error this API emits: the handlers above and the endpoint filters both
// go through IProblemDetailsService, so traceId and instance are applied in exactly one place.
builder.Services.AddEnterpriseProblemDetails();

// Singletons
// TimeProvider is injected rather than read statically so the permission cache's absolute expiry
// is testable without wall-clock delays.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IConversationLockService, ConversationLockService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IGraphService, GraphService>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<IDocumentIntelligenceService, DocumentIntelligenceService>();

// Document text extraction. Registering an IDocumentTextExtractor is all it takes to support a new
// format: the factory, the upload validator and the file-extensions endpoint all read the set of
// accepted formats from these registrations. The chunker is a singleton because constructing its
// tokenizer loads a large vocabulary.
builder.Services.AddSingleton<ILegacyWordConverter, DocSharpLegacyWordConverter>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentIntelligenceTextExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, PresentationTextExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractorFactory, DocumentTextExtractorFactory>();
builder.Services.AddSingleton<ITextChunker, TokenTextChunker>();

// Keep other services as Scoped
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IModelService, ModelService>();
builder.Services.AddScoped<IMcpServerService, McpServerService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IUserService, UserService>();

// Fluent Validators
builder.Services.AddValidatorsFromAssemblies(AppDomain.CurrentDomain.GetAssemblies());

var app = builder.Build();

// Skipped in the "Testing" environment: integration tests create the schema themselves
// via EnsureCreated (an ungated Migrate() would leave a DB with only __EFMigrationsHistory,
// making EnsureCreated a no-op) and must not reach out to Cosmos.
if (!app.Environment.IsEnvironment("Testing"))
{
    // Apply database migrations at startup
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<EnterpriseGptDbContext>();

        // This will create the database if it doesn't exist and apply all pending migrations
        context.Database.Migrate();

        app.Logger.LogInformation("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "An error occurred while migrating the database");
        throw; // Rethrow to prevent app startup if migration fails
    }

    // Apply cosmos DB migrations or setup if needed
    var cosmosClient = services.GetRequiredService<CosmosClient>();
    var database = await cosmosClient.CreateDatabaseIfNotExistsAsync(cosmosDatabaseId);
    await database.Database.CreateContainerIfNotExistsAsync(cosmosContainerId, "/userId");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors("AllowSpecificOrigins");

// .NET 10 default: diagnostics suppressed when a handler returns true; handlers log explicitly.
app.UseExceptionHandler();

// Must stay inside UseExceptionHandler. An exception propagates past this middleware without its
// post-processing running, so the bodiless 499 the cancellation handler produces is never decorated
// with a problem body the disconnected client could not read anyway. Ahead of authentication so it
// wraps the 401 challenge, and ahead of endpoint execution so it wraps the routing 404.
app.UseStatusCodePages();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapDocumentEndpoints();

app.MapModelEndpoints();

app.MapMcpEndpoints();

app.MapPermissionEndpoints();

app.MapUserEndpoints();

app.Run();

/// <summary>
/// Exposes the implicit <c>Program</c> class generated by top-level statements so
/// integration tests can reference it via <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}
