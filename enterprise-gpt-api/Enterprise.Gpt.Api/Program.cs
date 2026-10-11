using Enterprise.Gpt.Api.Configuration;
using Enterprise.Gpt.Api.Configuration.Providers;
using Enterprise.Gpt.Api.Endpoints;
using Enterprise.Gpt.Api.Health;
using Enterprise.Gpt.Api.Middleware;
using Enterprise.Gpt.Api.Observability;
using Enterprise.Gpt.Api.Problems;
using Enterprise.Gpt.Api.Startup;
using Enterprise.Gpt.Repository.Blob;
using Enterprise.Gpt.Repository.Cosmos;
using Enterprise.Gpt.Repository.Sql;

var builder = WebApplication.CreateBuilder(args);

// First: builder.Configuration rebuilds on each source added, so this loads the vault, and fails the host if
// it cannot, before anything reads a secret from it.
var keyVault = builder.AddEnterpriseGptKeyVault();

builder.Services
    .AddEnterpriseGptAuthentication(builder.Configuration)
    .AddEnterpriseGptSecurity()
    .AddEnterpriseGptSqlPersistence(builder.Configuration);

// After the DbContext it persists its key ring in, and after the vault whose key wraps it.
builder.AddEnterpriseGptDataProtection(keyVault);

builder.Services
    .AddEnterpriseGptCosmosPersistence(builder.Configuration)
    .AddEnterpriseGptBlobPersistence(builder.Configuration)
    .AddEnterpriseGptOpenApi()
    .AddEnterpriseGptCors(builder.Configuration)
    .AddEnterpriseGptChatClients(builder.Configuration)
    .AddDocumentIntelligenceProvider(builder.Configuration)
    .AddGraphProvider(builder.Configuration)
    .AddEnterpriseGptBackgroundJobs(builder.Configuration)
    .AddConversations(builder.Configuration)
    .AddDocuments(builder.Configuration)
    .AddAgents(builder.Configuration)
    .AddExports(builder.Configuration)
    .AddMcpServers(builder.Configuration)
    .AddPermissions(builder.Configuration)
    .AddChatModels()
    .AddProjects()
    .AddReports(builder.Configuration)
    .AddUsers()
    .AddEnterpriseGptValidation()
    .AddEnterpriseGptProblemDetails()
    .AddEnterpriseGptRequestLogging(builder.Configuration)
    .AddEnterpriseGptTelemetry(builder.Configuration)
    .AddEnterpriseGptHealthChecks();

var app = builder.Build();

KeyVaultConfiguration.LogStatus(keyVault, app.Logger);

await app.RunStartupBootstrappersAsync();

app.MapEnterpriseGptOpenApi();

app.UseHttpsRedirection();

app.UseEnterpriseGptCors();

// Outside UseExceptionHandler: the handler writes the response and returns normally, so the status is final
// when next() comes back. First in the pipeline so it can EnableBuffering before anything reads the body.
app.UseEnterpriseGptRequestLogging();

app.UseExceptionHandler();

// Inside UseExceptionHandler, so the bodiless 499 is never decorated, and ahead of authentication and routing,
// so the 401 challenge and the unrouted 404 carry a problem body.
app.UseStatusCodePages();

app.UseAuthentication();

app.UseAuthorization();

app.MapEnterpriseGptHealthChecks();

app.MapConversationEndpoints();

app.MapDocumentEndpoints();

app.MapModelEndpoints();

app.MapProjectEndpoints();

app.MapMcpEndpoints();

app.MapReportEndpoints();

app.MapPermissionEndpoints();

app.MapUserEndpoints();

await app.RunAsync();

/// <summary>
/// Exposes the implicit <c>Program</c> class to <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program
{
}
