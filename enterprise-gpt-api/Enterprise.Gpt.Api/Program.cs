using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Andes.Extensions.AI;
using Anthropic;
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
using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Api.Observability;
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
using Enterprise.Gpt.Service.Rendering;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
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


// Registered before the chat clients because UseToolTracking() collects every IChatProgressObserver
// in the container when the client is built. This one is how a completed request's usage report
// reaches the code that started it — including for a turn the user cancelled, whose report never
// reaches the response stream.
builder.Services.AddSingleton<IChatProgressObserver, ChatUsageObserver>();

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

// Per-tool token accounting and streamed activity, shared by every provider for the same reason as
// the function-invocation settings above. Both classifiers are installed so a tool is reported as
// what it is — an MCP server's tool, or an agent exposed as a function — rather than as an
// anonymous function; they stack, with agents short-circuiting and everything else falling through
// to the MCP classifier. Nothing turns tool arguments on: the middleware's default is to keep
// prompt content, arguments and results out of progress events, and that stands.
/// <summary>
/// Rejects a calibration multiplier that is not positive or exceeds the configured ceiling, so a
/// mistyped value fails at boot rather than silently distorting every stored count and budget.
/// </summary>
static bool ValidateCalibrationMultipliers(TokenEstimationOptions options)
{
    return options.CalibrationMultipliers.Values.All(multiplier =>
        multiplier > 0 && multiplier <= options.MaxCalibrationMultiplier);
}

static void ConfigureToolTracking(ToolTrackingOptions options)
{
    options.UseMcpToolClassification();
    options.UseAgentToolClassification();
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
    // Named source rather than the library's default: TelemetryRegistration has to register the same
    // name for these spans to be exported, and a default that changes with a package bump would take
    // the LLM traces off the Application Insights map without any build error.
    .UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName)
    // Must precede UseFunctionInvocation. The tracker works by wrapping the tools before the
    // function-invoking client executes them; reversed, that client runs the unwrapped tools and
    // the tracker sees nothing but text — no scopes, no per-tool usage, no progress.
    .UseToolTracking(ConfigureToolTracking)
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
        .UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName)
        .UseToolTracking(ConfigureToolTracking)
        .UseFunctionInvocation(null, ConfigureFunctionInvocation);
}

// 3) Anthropic under key "anthropic", off unless Anthropic:Enabled is set. Same gating as Bedrock, so
// a deployment that does not use Anthropic needs no Anthropic settings and a half-filled section
// fails at startup rather than on the first conversation.
builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "Anthropic:ApiKey is required when Anthropic:Enabled is true.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.DefaultModelId),
        "Anthropic:DefaultModelId is required when Anthropic:Enabled is true.")
    // The section three blocks up takes Bedrock ids, and an 'anthropic.'-prefixed one copied across
    // is the mistake that section invites. Caught here rather than as a 404 on the first turn.
    .Validate(options => !options.Enabled
        || !options.DefaultModelId.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase),
        "Anthropic:DefaultModelId must be a bare Anthropic model id; the 'anthropic.' prefix belongs to Bedrock.")
    .Validate(options => !options.Enabled || options.DefaultMaxOutputTokens > 0,
        "Anthropic:DefaultMaxOutputTokens must be greater than zero.")
    .Validate(options => !options.Enabled || options.Timeout > TimeSpan.Zero,
        "Anthropic:Timeout must be greater than zero.")
    .Validate(options => !options.Enabled || options.MaxRetries >= 0,
        "Anthropic:MaxRetries cannot be negative.")
    // Null-guarded before the lookup: the sets compare case-insensitively, and that comparer throws
    // on a null rather than reporting a miss, which would surface as a startup crash instead of the
    // message below.
    .Validate(options => !options.Enabled
        || (!string.IsNullOrWhiteSpace(options.Thinking) && AnthropicOptions.ThinkingModes.Contains(options.Thinking)),
        "Anthropic:Thinking must be 'adaptive' or 'disabled'.")
    .Validate(options => !options.Enabled
        || (!string.IsNullOrWhiteSpace(options.Effort) && AnthropicOptions.EffortLevels.Contains(options.Effort)),
        "Anthropic:Effort must be one of low, medium, high, xhigh, max.")
    // Disabled thinking is accepted only at 'high' effort or below; above it the model rejects every
    // request with a 400. Caught here so the combination cannot reach a conversation.
    .Validate(options => !options.Enabled || options.IsThinkingEffortCombinationValid,
        "Anthropic:Thinking 'disabled' is not accepted with Anthropic:Effort 'xhigh' or 'max'.")
    .ValidateOnStart();

if (builder.Configuration.GetValue<bool>($"{AnthropicOptions.SectionName}:Enabled"))
{
    // Registered in its own right, and as a singleton, because the SDK asks for exactly that: each
    // client carries its own connection and thread pools, so one per request would exhaust both.
    // Unlike the Bedrock runtime client there is no disposal to place — AnthropicClient is not
    // IDisposable, and the HTTP resources it holds are released only through the SDK's opt-in
    // ClientOptions.DisposeHttpResources(), which a process-lifetime singleton never needs.
    builder.Services.AddSingleton<IAnthropicClient>(sp =>
    {
        var anthropicOptions = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;

        // Assigning ApiKey is also what suppresses the SDK's environment and profile credential
        // resolution, so an ANTHROPIC_API_KEY that happens to be set on the host cannot authenticate
        // a deployment whose configured key is wrong.
        return new AnthropicClient
        {
            ApiKey = anthropicOptions.ApiKey,
            Timeout = anthropicOptions.Timeout,
            MaxRetries = anthropicOptions.MaxRetries
        };
    });

    builder.Services.AddKeyedChatClient(
            ChatClientKeys.Anthropic,
            sp =>
            {
                var anthropicOptions = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;

                return sp.GetRequiredService<IAnthropicClient>()
                         .AsIChatClient(anthropicOptions.DefaultModelId, anthropicOptions.DefaultMaxOutputTokens);
            }
        )
        .UseOpenTelemetry()
        .UseToolTracking(ConfigureToolTracking)
        .UseFunctionInvocation(null, ConfigureFunctionInvocation)
        // Last in the chain, which is innermost: ChatClientBuilder applies its factories in reverse,
        // so this is the client closest to the SDK and its ChatOptions are the ones the bridge reads.
        // Spelled out rather than via ConfigureOptions(...) because only this overload reaches the
        // container, and the settings have to come from the validated IOptions rather than a second
        // bind of the same section.
        .Use((innerClient, services) => new ConfigureOptionsChatClient(
            innerClient,
            AnthropicChatDefaults.Create(services.GetRequiredService<IOptions<AnthropicOptions>>().Value)));
}

builder.Services.AddSingleton<IChatClientResolver, ChatClientResolver>();

// Per-message context estimation. Registered unconditionally, unlike the chat clients above: an
// estimator is pure CPU and needs no credentials, so there is nothing for a deployment to enable.
// Singletons because building a tokenizer loads and indexes a large vocabulary.
builder.Services.AddOptions<TokenEstimationOptions>()
    .Bind(builder.Configuration.GetSection(TokenEstimationOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.FallbackEncoding),
        "TokenEstimation:FallbackEncoding must be configured.")
    .Validate(options => options.PerToolSchemaTokens >= 0,
        "TokenEstimation:PerToolSchemaTokens must not be negative.")
    .Validate(options => options.PerMessageFramingTokens >= 0,
        "TokenEstimation:PerMessageFramingTokens must not be negative.")
    .Validate(options => options.MaxCalibrationMultiplier > 0,
        "TokenEstimation:MaxCalibrationMultiplier must be greater than zero.")
    .Validate(ValidateCalibrationMultipliers)
    .ValidateOnStart();
builder.Services.AddSingleton<TiktokenTokenizerCache>();
builder.Services.AddKeyedSingleton<ITokenEstimator>(ChatClientKeys.AzureAIFoundry, (sp, _) =>
    new TiktokenTokenEstimator(Providers.AzureOpenAI, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
builder.Services.AddKeyedSingleton<ITokenEstimator>(ChatClientKeys.AmazonBedrock, (sp, _) =>
    new TiktokenTokenEstimator(Providers.AmazonBedrock, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
builder.Services.AddKeyedSingleton<ITokenEstimator>(ChatClientKeys.Anthropic, (sp, _) =>
    new TiktokenTokenEstimator(Providers.Anthropic, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
// The unkeyed default the resolver falls back to for a provider with no estimator of its own.
builder.Services.AddSingleton<ITokenEstimator>(sp =>
    new TiktokenTokenEstimator(Guid.Empty, sp.GetRequiredService<TiktokenTokenizerCache>(), sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
builder.Services.AddSingleton<ITokenEstimatorResolver, TokenEstimatorResolver>();
builder.Services.AddSingleton<IPromptEstimator, PromptEstimator>();

// Singleton because building the pipeline is per-process configuration; the per-render writer
// inside is what stays per call.
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

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

// Document retrieval (RAG) tuning. Over-fetching is what gives rank fusion and the per-document cap
// something to choose between, so a candidate count at or below the result count is a configuration
// error rather than a conservative setting.
builder.Services.AddOptions<DocumentRetrievalOptions>()
    .Bind(builder.Configuration.GetSection(DocumentRetrievalOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.CandidateCount >= options.MaxResults,
        "Documents:Retrieval:CandidateCount must be at least Documents:Retrieval:MaxResults.")
    .Validate(options => options.LexicalCandidateCount >= options.MaxResults,
        "Documents:Retrieval:LexicalCandidateCount must be at least Documents:Retrieval:MaxResults.")
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

// Register Azure Cosmos DB. Validated at startup because a missing database or container id used
// to surface as a null-forgiving bang here and then as a failure on a user's first conversation.
// CosmosDb:ContainerId is rejected rather than ignored: it names the retired one-document-per-
// conversation container, whose shape this application can no longer read, so a deployment that
// still configures it has not performed the cutover and must not start.
builder.Services.AddOptions<CosmosOptions>()
    .Bind(builder.Configuration.GetSection(CosmosOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "CosmosDb:ConnectionString must be configured.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabaseId),
        "CosmosDb:DatabaseId must be configured.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.TranscriptContainerId),
        "CosmosDb:TranscriptContainerId must be configured.")
    .Validate(options => options.ContainerId is null,
        "CosmosDb:ContainerId is retired and names a transcript shape this application cannot read. "
        + "Remove it and configure CosmosDb:TranscriptContainerId with the new container.")
    .ValidateOnStart();
builder.Services.AddSingleton(provider =>
{
    var cosmosOptions = provider.GetRequiredService<IOptions<CosmosOptions>>().Value;

    // System.Text.Json rather than the SDK default, so the [JsonPropertyName] annotations on the
    // document types are the naming contract instead of coinciding with it.
    var cosmosClientOptions = new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.Options
    };

    return new CosmosClient(cosmosOptions.ConnectionString, cosmosClientOptions);
});
builder.Services.AddSingleton<IAzureCosmosService, AzureCosmosService>();

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

// Observability. The middleware writes through ILogger and names no backend; AddEnterpriseTelemetry is
// the one place Application Insights appears, and no-ops when no connection string is configured.
builder.Services.AddRequestLogging(builder.Configuration);
builder.Services.AddEnterpriseTelemetry(builder.Configuration);

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
builder.Services.AddScoped<IDocumentRetrievalService, DocumentRetrievalService>();
builder.Services.AddScoped<IModelService, ModelService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
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

    // Create the transcript container when absent and verify its partition key paths when
    // present. A container whose paths differ cannot be addressed by this application, so the
    // bootstrap fails startup rather than serving reads that silently match nothing.
    var cosmosClient = services.GetRequiredService<CosmosClient>();
    var cosmosOptions = services.GetRequiredService<IOptions<CosmosOptions>>().Value;
    await CosmosContainerBootstrap.EnsureTranscriptContainerAsync(cosmosClient, cosmosOptions, app.Logger);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors("AllowSpecificOrigins");

// Outside UseExceptionHandler on purpose: the handler writes the response and returns normally, so the
// status is already final when next() comes back — inside, every failure would look like an in-flight
// exception and duplicate what the handlers log. Being first in the app pipeline is also what lets it
// call EnableBuffering before anything reads the body. It sits ahead of UseAuthentication, so the caller
// is anonymous on the way in and the user id is only reported on the way out. CORS preflights are
// short-circuited above and never reach it, which is the intended noise reduction.
app.UseRequestLogging();

// .NET 10 default: diagnostics suppressed when a handler returns true; handlers log explicitly.
app.UseExceptionHandler();

// Must stay inside UseExceptionHandler. An exception propagates past this middleware without its
// post-processing running, so the bodiless 499 the cancellation handler produces is never decorated
// with a problem body the disconnected client could not read anyway. Ahead of authentication so it
// wraps the 401 challenge, and ahead of endpoint execution so it wraps the routing 404.
app.UseStatusCodePages();

app.UseAuthentication();

app.UseAuthorization();

app.MapConversationEndpoints();

app.MapDocumentEndpoints();

app.MapModelEndpoints();

app.MapProjectEndpoints();

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
