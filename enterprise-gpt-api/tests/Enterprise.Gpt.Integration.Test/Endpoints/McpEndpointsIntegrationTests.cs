using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class McpEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetMcpServersAndPermissionsAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static CreateMcpServerActionDto CreateRequest(
        string name = "it-mcp-created",
        string url = "https://mcp.example.com/sse",
        McpAuthTypes authType = McpAuthTypes.None,
        string? scope = null)
    {
        return new CreateMcpServerActionDto
        {
            Name = name,
            Description = $"{name} description",
            Url = url,
            AuthType = authType,
            Scope = scope
        };
    }

    private static UpdateMcpServerActionDto UpdateRequest(
        string name = "it-mcp-updated",
        string url = "https://mcp-updated.example.com/sse",
        McpAuthTypes authType = McpAuthTypes.None,
        string? scope = null)
    {
        return new UpdateMcpServerActionDto
        {
            Name = name,
            Description = $"{name} description",
            Url = url,
            AuthType = authType,
            Scope = scope
        };
    }


    [Theory]
    [InlineData("GET", "api/mcps")]
    [InlineData("GET", "api/mcps/all")]
    [InlineData("GET", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/mcps")]
    [InlineData("PUT", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    public async Task McpRoutes_AnonymousUser_ReturnsUnauthorized(string method, string route)
    {
        using var client = _fixture.Factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Theory]
    [InlineData("GET", "api/mcps/all")]
    [InlineData("GET", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/mcps")]
    [InlineData("PUT", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/mcps/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    public async Task AdminMcpRoutes_RegularUser_ReturnsForbidden(string method, string route)
    {
        using var client = _fixture.Factory.CreateUserClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT")
        {
            // A well-formed body so the request reaches the admin filter instead of failing
            // JSON binding first.
            request.Content = JsonContent.Create(CreateRequest(name: "it-mcp-forbidden"));
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task CreateMcpServer_AdminUser_CreatesServerAndLinkedPermission()
    {
        var request = CreateRequest(name: "it-mcp-create");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/mcps", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<McpServerDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(request.Name, created.Name);
        Assert.Equal(request.Url, created.Url);
        Assert.NotNull(created.PermissionId);
        Assert.Equal($"/api/mcps/{created.Id}", response.Headers.Location?.ToString());

        var server = await _fixture.FindMcpServerAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(server);
        Assert.Equal(TestUsers.AdminUserId, server.CreatedById);
        Assert.Null(server.DateDeactivated);

        var permission = await _fixture.FindPermissionByMcpServerAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(permission);
        Assert.Equal(created.PermissionId, permission.Id);
        Assert.Equal(request.Name, permission.Name);
        Assert.Equal(created.Id, permission.McpServerId);
        Assert.Null(permission.DateDeactivated);
    }

    [Fact]
    public async Task CreateMcpServer_DuplicateName_ReturnsBadRequest()
    {
        await _fixture.AddMcpServerAsync("it-mcp-dup", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "api/mcps", CreateRequest(name: "it-mcp-dup"), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Equal(
            "The name 'it-mcp-dup' is already in use by an MCP server or permission.",
            Assert.Single(problem.Errors["Name"]));
    }

    [Fact]
    public async Task CreateMcpServer_EntraIdWithoutScope_ReturnsBadRequest()
    {
        var request = CreateRequest(name: "it-mcp-noscope", authType: McpAuthTypes.EntraIdOnBehalfOf, scope: null);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/mcps", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task CreateMcpServer_ScopeWithNoneAuthType_ReturnsBadRequest()
    {
        var request = CreateRequest(name: "it-mcp-scope", authType: McpAuthTypes.None, scope: "api://it/access_as_user");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/mcps", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/rooted/path")]
    [InlineData("ftp://mcp.example.com/tools")]
    [InlineData("not a url")]
    public async Task CreateMcpServer_InvalidUrl_ReturnsBadRequest(string url)
    {
        var request = CreateRequest(name: "it-mcp-badurl", url: url);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/mcps", request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains(problem.Errors["Url"], message => message.Contains("absolute http or https URI"));
    }

    [Fact]
    public async Task GetAllMcpServers_AdminUser_IncludesDeactivatedServers()
    {
        var (activeId, _) = await _fixture.AddMcpServerAsync(
            "it-mcp-active", cancellationToken: TestContext.Current.CancellationToken);
        var (deactivatedId, _) = await _fixture.AddMcpServerAsync(
            "it-mcp-inactive", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var servers = await client.GetFromJsonAsync<List<McpServerDto>>("api/mcps/all", TestContext.Current.CancellationToken);

        Assert.NotNull(servers);
        Assert.Contains(servers, x => x.Id == activeId && x.DateDeactivated is null);
        var deactivated = Assert.Single(servers, x => x.Id == deactivatedId);
        Assert.NotNull(deactivated.DateDeactivated);
    }

    [Fact]
    public async Task GetMcpServer_ExistingServer_ReturnsServerWithPermissionId()
    {
        var (serverId, permissionId) = await _fixture.AddMcpServerAsync(
            "it-mcp-read", authType: 2, scope: "api://it/access_as_user",
            cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var server = await client.GetFromJsonAsync<McpServerDto>($"api/mcps/{serverId}", TestContext.Current.CancellationToken);

        Assert.NotNull(server);
        Assert.Equal(serverId, server.Id);
        Assert.Equal("it-mcp-read", server.Name);
        Assert.Equal("https://mcp.example.com/sse", server.Url);
        Assert.Equal(McpAuthTypes.EntraIdOnBehalfOf, server.AuthType);
        Assert.Equal("api://it/access_as_user", server.Scope);
        Assert.Equal(permissionId, server.PermissionId);
    }

    [Fact]
    public async Task GetMcpServer_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync($"api/mcps/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("MCP server not found.", problem.Detail);
    }

    [Fact]
    public async Task UpdateMcpServer_Rename_SyncsLinkedPermissionName()
    {
        var (serverId, permissionId) = await _fixture.AddMcpServerAsync(
            "it-mcp-old-name", cancellationToken: TestContext.Current.CancellationToken);
        var request = UpdateRequest(name: "it-mcp-new-name");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync($"api/mcps/{serverId}", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<McpServerDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(request.Name, updated.Name);
        Assert.Equal(request.Url, updated.Url);
        Assert.Equal(permissionId, updated.PermissionId);

        Assert.NotNull(permissionId);
        var permission = await _fixture.FindPermissionAsync(permissionId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(permission);
        Assert.Equal("it-mcp-new-name", permission.Name);
        Assert.Null(permission.DateDeactivated);
    }

    [Fact]
    public async Task UpdateMcpServer_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/mcps/{Guid.NewGuid()}", UpdateRequest(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("MCP server not found.", problem.Detail);
    }

    [Fact]
    public async Task DeleteMcpServer_AdminUser_CascadesToPermissionAndGrants()
    {
        var (serverId, permissionId) = await _fixture.AddMcpServerAsync(
            "it-mcp-doomed", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(permissionId);
        await _fixture.AddUserPermissionAsync(
            TestUsers.RegularUserId, permissionId.Value, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync($"api/mcps/{serverId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var server = await _fixture.FindMcpServerAsync(serverId, TestContext.Current.CancellationToken);
        Assert.NotNull(server);
        Assert.NotNull(server.DateDeactivated);

        var permission = await _fixture.FindPermissionAsync(permissionId.Value, TestContext.Current.CancellationToken);
        Assert.NotNull(permission);
        Assert.NotNull(permission.DateDeactivated);

        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, permissionId.Value, TestContext.Current.CancellationToken);
        var grant = Assert.Single(grants);
        Assert.NotNull(grant.DateDeactivated);
    }

    [Fact]
    public async Task DeleteMcpServer_SecondDelete_ReturnsNotFound()
    {
        var (serverId, _) = await _fixture.AddMcpServerAsync(
            "it-mcp-doomed-twice", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();
        var firstResponse = await client.DeleteAsync($"api/mcps/{serverId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await client.DeleteAsync($"api/mcps/{serverId}", TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(secondResponse, HttpStatusCode.NotFound);
        Assert.Equal("MCP server not found.", problem.Detail);
    }

    [Fact]
    public async Task GetMcps_UserWithoutGrants_ReturnsEmptyList()
    {
        await _fixture.AddMcpServerAsync("it-mcp-ungranted", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var mcps = await client.GetFromJsonAsync<List<McpDto>>("api/mcps", TestContext.Current.CancellationToken);

        Assert.NotNull(mcps);
        Assert.Empty(mcps);
    }

    [Fact]
    public async Task GetMcps_GrantedUser_ReturnsOnlyGrantedServers()
    {
        var (grantedId, grantedPermissionId) = await _fixture.AddMcpServerAsync(
            "it-mcp-granted", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddMcpServerAsync("it-mcp-other", cancellationToken: TestContext.Current.CancellationToken);
        using var adminClient = _fixture.Factory.CreateAdminClient();
        var grantResponse = await adminClient.PostAsync(
            $"api/users/{TestUsers.RegularUserId}/permissions/{grantedPermissionId}",
            content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);
        using var client = _fixture.Factory.CreateUserClient();

        var mcps = await client.GetFromJsonAsync<List<McpDto>>("api/mcps", TestContext.Current.CancellationToken);

        Assert.NotNull(mcps);
        var mcp = Assert.Single(mcps);
        Assert.Equal(grantedId, mcp.Id);
        Assert.Equal("it-mcp-granted", mcp.Name);
        Assert.Equal("it-mcp-granted description", mcp.Description);
    }

    [Fact]
    public async Task GetMcps_DeactivatedServer_IsExcludedEvenWithActiveGrant()
    {
        var (_, permissionId) = await _fixture.AddMcpServerAsync(
            "it-mcp-dead", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(permissionId);
        // The grant row itself stays active, isolating the server/permission-active filters.
        await _fixture.AddUserPermissionAsync(
            TestUsers.RegularUserId, permissionId.Value, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateUserClient();

        var mcps = await client.GetFromJsonAsync<List<McpDto>>("api/mcps", TestContext.Current.CancellationToken);

        Assert.NotNull(mcps);
        Assert.Empty(mcps);
    }

    [Fact]
    public async Task GetMcps_AdminUserWithoutGrant_IsAlsoPermissionFiltered()
    {
        await _fixture.AddMcpServerAsync("it-mcp-admin-hidden", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var mcps = await client.GetFromJsonAsync<List<McpDto>>("api/mcps", TestContext.Current.CancellationToken);

        Assert.NotNull(mcps);
        Assert.Empty(mcps);
    }
}
