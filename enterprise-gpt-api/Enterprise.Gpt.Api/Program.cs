using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Andes.Extensions.AI;
using Anthropic;
using Azure;
using Azure.AI.DocumentIntelligence;
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
using OpenAI;
using Enterprise.Gpt.Api.Chat;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Api.ExceptionHandlers;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.BackgroundJobs;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Chat;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Converters;
using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Extraction;
using Enterprise.Gpt.Service.Rendering;
using Enterprise.Gpt.Service.Serialization;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tokenization;
using Enterprise.Gpt.Service.Tool;
using Enterprise.Gpt.Service.Transcripts;
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
static void ConfigureToolTracking(ToolTrackingOptions options)
{
    options.UseMcpToolClassification();
    options.UseAgentToolClassification();
}

// 1) Azure OpenAI under key "azure-open-ai". Unlike the other three providers this one has no
// Enabled flag — it always serves the default model and owns the embedding generator, so missing
// settings are a broken deployment rather than an opted-out one, and the validators run
// unconditionally.
builder.Services.AddOptions<AzureOpenAIOptions>()
    .Bind(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName))
    .Validate(options => options.IsUrlAbsolute,
        "AzureOpenAI:Url must be an absolute http or https URI, for example https://my-resource.services.ai.azure.com/.")
    // The client appends the v1 path itself. Configuring it here would send every request to
    // /openai/v1/openai/v1/…, and the 404 that produces names neither the setting nor the cause.
    .Validate(options => options.IsUrlResourceRoot,
        "AzureOpenAI:Url must be the resource root, without /openai/v1 — the client appends it.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey),
        "AzureOpenAI:ApiKey is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DefaultModel),
        "AzureOpenAI:DefaultModel is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.EmbeddingModel),
        "AzureOpenAI:EmbeddingModel is required.")
    // Null-guarded before the lookup: the sets compare case-insensitively, and that comparer throws
    // on a null rather than reporting a miss, which would surface as a startup crash instead of the
    // message below.
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReasoningSummary)
        && AzureOpenAIOptions.ReasoningSummaries.Contains(options.ReasoningSummary),
        "AzureOpenAI:ReasoningSummary must be one of auto, concise, detailed, none.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReasoningEffort)
        && AzureOpenAIOptions.ReasoningEfforts.Contains(options.ReasoningEffort),
        "AzureOpenAI:ReasoningEffort must be one of minimal, low, medium, high.")
    .ValidateOnStart();

// The Responses API rather than Chat Completions, because reasoning summaries exist only on that
// surface: Azure returns no reasoning content over Chat Completions, so the activity timeline's
// reasoning region had nothing to render. That is the whole difference between this provider and
// the Foundry one below, which shares the endpoint and the SDK but not the surface. The v1 endpoint
// is OpenAI-compatible and takes the deployment name in `model`.
builder.Services.AddKeyedChatClient(
        ChatClientKeys.AzureOpenAI,
        sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

            // MEAI001: the Responses adapter is still marked experimental in
            // Microsoft.Extensions.AI.OpenAI 10.9.0, as OPENAI001 is in OpenAI 2.12.0 — see the
            // header of AzureOpenAIChatDefaults.cs for why no upgrade retires either. Suppressed at
            // the one call site rather than project-wide, so a second experimental API cannot slip
            // in unnoticed behind it.
#pragma warning disable OPENAI001, MEAI001
            return new OpenAIClient(
                    new ApiKeyCredential(settings.ApiKey),
                    new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
                .GetResponsesClient()
                .AsIChatClient(settings.DefaultModel);
#pragma warning restore OPENAI001, MEAI001
        }
    )
    // Named source rather than the library's default: TelemetryRegistration has to register the same
    // name for these spans to be exported, and a default that changes with a package bump would take
    // the LLM traces off the Application Insights map without any build error.
    .UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName)
    // Must precede UseFunctionInvocation. The tracker works by wrapping the tools before the
    // function-invoking client executes them; reversed, that client runs the unwrapped tools and
    // the tracker sees nothing but text — no scopes, no per-tool usage, no progress.
    .UseToolTracking(ConfigureToolTracking)
    .UseFunctionInvocation(null, ConfigureFunctionInvocation)
    // Last in the chain, which is innermost — see the Anthropic registration below for why that
    // matters. It is also what makes clearing ConversationId safe: the telemetry client above has
    // already read it by the time this runs.
    .Use((innerClient, services) => new ConfigureOptionsChatClient(
        innerClient,
        AzureOpenAIChatDefaults.Create(services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value)));

// 2) Azure AI Foundry under key "azure-ai-foundry", off unless AzureAIFoundry:Enabled is set. Same
// resource and same v1 endpoint as the provider above, reached over Chat Completions instead of
// Responses — which is what lets it serve Foundry Models (DeepSeek, Llama, Mistral) alongside Azure
// OpenAI deployments, at the cost of reasoning. Its own section rather than a flag on the one above
// because the two carry different credentials in every deployment that uses both.
builder.Services.AddOptions<AzureAIFoundryOptions>()
    .Bind(builder.Configuration.GetSection(AzureAIFoundryOptions.SectionName))
    .Validate(options => !options.Enabled || options.IsUrlAbsolute,
        "AzureAIFoundry:Url must be an absolute http or https URI when AzureAIFoundry:Enabled is true, for example https://my-resource.services.ai.azure.com/.")
    .Validate(options => !options.Enabled || options.IsUrlResourceRoot,
        "AzureAIFoundry:Url must be the resource root, without /openai/v1 — the client appends it — when AzureAIFoundry:Enabled is true.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "AzureAIFoundry:ApiKey is required when AzureAIFoundry:Enabled is true.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.DefaultModel),
        "AzureAIFoundry:DefaultModel is required when AzureAIFoundry:Enabled is true.")
    .ValidateOnStart();

if (builder.Configuration.GetValue<bool>($"{AzureAIFoundryOptions.SectionName}:Enabled"))
{
    builder.Services.AddKeyedChatClient(
            ChatClientKeys.AzureAIFoundry,
            sp =>
            {
                var settings = sp.GetRequiredService<IOptions<AzureAIFoundryOptions>>().Value;

                // No suppression here, and that is the point of the split: GetChatClient and its
                // AsIChatClient overload are both stable, so the experimental surface stays confined
                // to the Responses registration above.
                return new OpenAIClient(
                        new ApiKeyCredential(settings.ApiKey),
                        new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
                    .GetChatClient(settings.DefaultModel)
                    .AsIChatClient();
            }
        )
        .UseOpenTelemetry(sourceName: TelemetryRegistration.ChatTelemetrySourceName)
        .UseToolTracking(ConfigureToolTracking)
        .UseFunctionInvocation(null, ConfigureFunctionInvocation)
        // Last in the chain, which is innermost, so its ChatOptions are the ones the bridge reads —
        // see the Anthropic registration below.
        .Use((innerClient, _) => new ConfigureOptionsChatClient(
            innerClient,
            AzureAIFoundryChatDefaults.Create()));
}

// 3) Amazon Bedrock under key "amazonbedrock", off unless AmazonBedrock:Enabled is set. Validation is
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

// 4) Anthropic under key "anthropic", off unless Anthropic:Enabled is set. Same gating as Bedrock, so
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

// AI Embedding Generators. On the same OpenAIClient and v1 endpoint as the chat clients above,
// which is what let Azure.AI.OpenAI leave the solution entirely — it was this generator's last
// consumer, and its only stable release is long superseded. `/openai/v1/embeddings` is the
// OpenAI-compatible route to the same deployment on the same resource that
// `/openai/deployments/{model}/embeddings` reached before, so stored vectors are unaffected; the
// deployment named by EmbeddingModel is what determines them, not the route.
//
// Built from a factory rather than eagerly, so the validated options are the only source of the URL.
// Constructing it here would run `new Uri(...)` during service configuration — before Build(), and
// so before ValidateOnStart — and a blank AzureOpenAI:Url would surface as a bare
// UriFormatException from this line instead of the validator's message.
builder.Services.AddEmbeddingGenerator(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;

    return new OpenAIClient(
            new ApiKeyCredential(settings.ApiKey),
            new OpenAIClientOptions { Endpoint = settings.V1Endpoint })
        .GetEmbeddingClient(settings.EmbeddingModel)
        .AsIEmbeddingGenerator();
});

// The fabricated weather tool. Off unless a deployment asks for it: it answers with invented data,
// and attached to every production turn it is a tool the model would call and then answer from.
builder.Services.AddOptions<WeatherToolOptions>()
    .Bind(builder.Configuration.GetSection(WeatherToolOptions.SectionName))
    .ValidateDataAnnotations()
    // Not an error, because a staging environment turning it on is legitimate — but a deployment
    // answering users from invented data should have said so out loud at least once.
    .Validate(options =>
    {
        if (options.Enabled && builder.Environment.IsProduction())
        {
            Console.Error.WriteLine(
                "WARNING: Tools:Weather:Enabled is set in Production. The weather tool answers with " +
                "fabricated data and the model will report it to users as fact.");
        }

        return true;
    })
    .ValidateOnStart();

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

// Azure Cosmos DB, holding the conversation transcript. Bound and validated at startup because
// every value here is read once, when a container handle is built, so a missing id used to surface
// as a failure on the first conversation rather than at boot.
builder.Services.AddOptions<CosmosOptions>()
    .Bind(builder.Configuration.GetSection(CosmosOptions.SectionName))
    .ValidateDataAnnotations()
    // Messages name the configuration key an operator has to edit rather than the bound property,
    // and never echo the value: the connection string carries an account key.
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "CosmosDb:ConnectionString is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabaseId),
        "CosmosDb:DatabaseId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.TranscriptContainerId),
        "CosmosDb:TranscriptContainerId is required.")
    // The cutover gate. A configuration that still names the pre-cutover container is a deployment
    // that has not been migrated, and starting it would answer every transcript read with an empty
    // result — which reads as data loss rather than as a setting nobody removed.
    .Validate(_ => builder.Configuration[CosmosOptions.LegacyContainerKey] is null,
        $"{CosmosOptions.LegacyContainerKey} names the pre-cutover container, whose documents this application can no longer read. Remove the setting and set CosmosDb:TranscriptContainerId instead; see docs/conversations/transcript-cutover.md.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    var cosmos = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;

    // System.Text.Json rather than the SDK's Newtonsoft default: the document types carry
    // [JsonPropertyName] attributes that the default serializer never reads, so the stored names
    // agreed with them only because the SDK's camel-case policy happened to produce the same
    // result. A query predicate that did not match those names is a bug already recorded here.
    return new CosmosClient(cosmos.ConnectionString, new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateOptions()
    });
});
builder.Services.AddSingleton<ITranscriptStore, TranscriptStore>();

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

// Per-message token estimation. Validated at startup because a negative overhead term or a
// nonsensical calibration multiplier silently corrupts every stored token count and, through the
// context budget built on them, silently shrinks what is replayed to the model.
builder.Services.AddOptions<TokenEstimationOptions>()
    .Bind(builder.Configuration.GetSection(TokenEstimationOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.FallbackEncoding),
        "Tokenization:FallbackEncoding is required.")
    .Validate(
        options => options.ProviderCalibration.Values.All(multiplier => multiplier > 0 && multiplier <= options.MaxCalibrationMultiplier),
        "Every Tokenization:ProviderCalibration value must be greater than zero and at most Tokenization:MaxCalibrationMultiplier.")
    .Validate(
        options => options.ProviderCalibration.Keys.All(key => Guid.TryParse(key, out _)),
        "Every Tokenization:ProviderCalibration key must be a provider id in GUID form.")
    .ValidateOnStart();

builder.Services.AddSingleton<TokenizerProvider>();
builder.Services.AddSingleton<PromptOverheadCalculator>();

// One estimator per provider, keyed exactly as the chat clients are, so a provider's calibration
// multiplier is resolved the same way its client is. The vocabularies themselves are shared through
// the singleton TokenizerProvider, so four registrations do not mean four loaded vocabularies.
foreach (var (providerId, serviceKey) in Providers.ServiceKeys)
{
    var provider = providerId;
    builder.Services.AddKeyedSingleton<ITokenEstimator>(serviceKey, (sp, _) =>
        new TiktokenTokenEstimator(
            provider,
            sp.GetRequiredService<TokenizerProvider>(),
            sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
}

// The uncalibrated fallback the resolver hands back for a provider with no registration.
builder.Services.AddSingleton<ITokenEstimator>(sp =>
    new TiktokenTokenEstimator(
        Guid.Empty,
        sp.GetRequiredService<TokenizerProvider>(),
        sp.GetRequiredService<IOptions<TokenEstimationOptions>>()));
builder.Services.AddSingleton<ITokenEstimatorResolver, TokenEstimatorResolver>();

// Server-side markdown rendering, stored beside each message so an export is a read rather than a
// re-render. A singleton because building the pipeline is per-process configuration.
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();

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
builder.Services.AddScoped<IConversationExportService, ConversationExportService>();
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

    // Provision the Cosmos database and containers, and refuse to start against a transcript
    // container whose partition key path this application cannot address.
    var cosmosClient = services.GetRequiredService<CosmosClient>();
    var cosmosOptions = services.GetRequiredService<IOptions<CosmosOptions>>().Value;
    await CosmosBootstrapper.EnsureProvisionedAsync(cosmosClient, cosmosOptions, app.Logger, CancellationToken.None);
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
