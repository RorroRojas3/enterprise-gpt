using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Permission;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PermissionEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
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

    private static CreatePermissionActionDto CreateRequest(string name = "it-perm-created")
    {
        return new CreatePermissionActionDto
        {
            Name = name,
            Description = $"{name} description"
        };
    }

    private static UpdatePermissionActionDto UpdateRequest(string name = "it-perm-updated")
    {
        return new UpdatePermissionActionDto
        {
            Name = name,
            Description = $"{name} description"
        };
    }

    private static async Task<ErrorDto> ReadErrorAsync(HttpResponseMessage response, HttpStatusCode expectedStatusCode)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        Assert.Equal(expectedStatusCode, error.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        return error;
    }

    private async Task<PermissionDto> CreatePermissionAsync(HttpClient adminClient, string name)
    {
        var response = await adminClient.PostAsJsonAsync(
            "api/permissions", CreateRequest(name), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<PermissionDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        return created;
    }

    [Theory]
    [InlineData("GET", "api/permissions")]
    [InlineData("GET", "api/permissions/all")]
    [InlineData("GET", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/permissions")]
    [InlineData("PUT", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/users/b2b2b2b2-0000-4000-8000-000000000002/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/users/b2b2b2b2-0000-4000-8000-000000000002/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    public async Task PermissionRoutes_AnonymousUser_ReturnsUnauthorized(string method, string route)
    {
        using var client = _fixture.Factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "api/permissions")]
    [InlineData("GET", "api/permissions/all")]
    [InlineData("GET", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/permissions")]
    [InlineData("PUT", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("POST", "api/users/b2b2b2b2-0000-4000-8000-000000000002/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    [InlineData("DELETE", "api/users/b2b2b2b2-0000-4000-8000-000000000002/permissions/0e984725-c51c-4bf5-9960-e1c80e27aba0")]
    public async Task PermissionRoutes_RegularUser_ReturnsForbidden(string method, string route)
    {
        using var client = _fixture.Factory.CreateUserClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT" && route.StartsWith("api/permissions", StringComparison.Ordinal))
        {
            // A well-formed body so the request reaches the admin filter instead of failing
            // JSON binding first. The grant routes bind route values only, so they need none.
            request.Content = JsonContent.Create(CreateRequest(name: "it-perm-forbidden"));
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task CreatePermission_AdminUser_ReturnsCreatedAndPersistsAudit()
    {
        var request = CreateRequest(name: "it-perm-create");
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/permissions", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<PermissionDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(request.Name, created.Name);
        Assert.Equal(request.Description, created.Description);
        Assert.Null(created.McpServerId);
        Assert.Null(created.DateDeactivated);
        Assert.Equal($"/api/permissions/{created.Id}", response.Headers.Location?.ToString());

        var fetched = await client.GetFromJsonAsync<PermissionDto>(
            $"api/permissions/{created.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(request.Name, fetched.Name);

        var persisted = await _fixture.FindPermissionAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(TestUsers.AdminUserId, persisted.CreatedById);
        Assert.Null(persisted.McpServerId);
    }

    [Fact]
    public async Task CreatePermission_DuplicateName_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "api/permissions", CreateRequest(name: "Administrator"), TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            "The name 'Administrator' is already in use by a permission or MCP server.",
            Assert.Single(error.Errors));
    }

    [Fact]
    public async Task GetPermissions_AdminUser_ReturnsActivePermissions()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-listed");

        var permissions = await client.GetFromJsonAsync<List<PermissionDto>>(
            "api/permissions", TestContext.Current.CancellationToken);

        Assert.NotNull(permissions);
        Assert.Contains(permissions, x => x.Id == created.Id);
        Assert.Contains(permissions, x => x.Id == KnownIds.AdministratorPermissionId);
        Assert.All(permissions, x => Assert.Null(x.DateDeactivated));
    }

    [Fact]
    public async Task UpdatePermission_AdminUser_UpdatesFields()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-original");
        var request = UpdateRequest(name: "it-perm-renamed");

        var response = await client.PutAsJsonAsync(
            $"api/permissions/{created.Id}", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<PermissionDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(request.Name, updated.Name);
        Assert.Equal(request.Description, updated.Description);

        var fetched = await client.GetFromJsonAsync<PermissionDto>(
            $"api/permissions/{created.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(request.Name, fetched.Name);
        Assert.Equal(request.Description, fetched.Description);
    }

    [Fact]
    public async Task UpdatePermission_UnknownId_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/permissions/{Guid.NewGuid()}", UpdateRequest(), TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Permission not found.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task UpdatePermission_AdministratorPermission_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/permissions/{KnownIds.AdministratorPermissionId}", UpdateRequest(), TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("The built-in Administrator permission cannot be modified.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task DeletePermission_AdministratorPermission_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"api/permissions/{KnownIds.AdministratorPermissionId}", TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("The built-in Administrator permission cannot be modified.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task UpdatePermission_McpManagedPermission_ReturnsBadRequest()
    {
        var (_, permissionId) = await _fixture.AddMcpServerAsync(
            "it-perm-mcp-managed", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/permissions/{permissionId}", UpdateRequest(), TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            "This permission is managed by its MCP server and cannot be modified directly.",
            Assert.Single(error.Errors));
    }

    [Fact]
    public async Task DeletePermission_McpManagedPermission_ReturnsBadRequest()
    {
        var (_, permissionId) = await _fixture.AddMcpServerAsync(
            "it-perm-mcp-locked", cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"api/permissions/{permissionId}", TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            "This permission is managed by its MCP server and cannot be modified directly.",
            Assert.Single(error.Errors));
    }

    [Fact]
    public async Task DeletePermission_AdminUser_SoftDeletesAndCascadesGrants()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-doomed");
        var grantResponse = await client.PostAsync(
            $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}",
            content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);

        var response = await client.DeleteAsync($"api/permissions/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var permission = await _fixture.FindPermissionAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(permission);
        Assert.NotNull(permission.DateDeactivated);

        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, created.Id, TestContext.Current.CancellationToken);
        var grant = Assert.Single(grants);
        Assert.NotNull(grant.DateDeactivated);

        var getResponse = await client.GetAsync($"api/permissions/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePermission_SecondDelete_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-doomed-twice");
        var firstResponse = await client.DeleteAsync($"api/permissions/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await client.DeleteAsync($"api/permissions/{created.Id}", TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(secondResponse, HttpStatusCode.NotFound);
        Assert.Equal("Permission not found.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task GrantPermission_NewGrant_ReturnsNoContentAndInsertsActiveRow()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-grant");

        var response = await client.PostAsync(
            $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}",
            content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, created.Id, TestContext.Current.CancellationToken);
        var grant = Assert.Single(grants);
        Assert.Null(grant.DateDeactivated);
        Assert.Equal(TestUsers.AdminUserId, grant.CreatedById);
    }

    [Fact]
    public async Task GrantPermission_AlreadyGranted_IsIdempotent()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-idempotent");
        var route = $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}";
        var firstResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, created.Id, TestContext.Current.CancellationToken);
        var grant = Assert.Single(grants);
        Assert.Null(grant.DateDeactivated);
    }

    [Fact]
    public async Task RevokePermission_ActiveGrant_SoftDeletesRow()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-revoke");
        var route = $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}";
        var grantResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);

        var revokeResponse = await client.DeleteAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, created.Id, TestContext.Current.CancellationToken);
        var grant = Assert.Single(grants);
        Assert.NotNull(grant.DateDeactivated);
    }

    [Fact]
    public async Task RevokePermission_NoActiveGrant_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-revoke-twice");
        var route = $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}";
        var grantResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);
        var revokeResponse = await client.DeleteAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var secondRevokeResponse = await client.DeleteAsync(route, TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(secondRevokeResponse, HttpStatusCode.NotFound);
        Assert.Equal("The user holds no active grant of this permission.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task GrantPermission_AfterRevoke_InsertsFreshRow()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-regrant");
        var route = $"api/users/{TestUsers.RegularUserId}/permissions/{created.Id}";
        var grantResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);
        var revokeResponse = await client.DeleteAsync(route, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var regrantResponse = await client.PostAsync(route, content: null, TestContext.Current.CancellationToken);

        // Two rows for the pair with exactly one active proves the filtered unique index
        // permits re-grants while preserving revoked history.
        Assert.Equal(HttpStatusCode.NoContent, regrantResponse.StatusCode);
        var grants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, created.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, grants.Count);
        Assert.Single(grants, x => x.DateDeactivated is null);
    }

    [Fact]
    public async Task GrantPermission_UnknownUser_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var created = await CreatePermissionAsync(client, "it-perm-no-user");

        var response = await client.PostAsync(
            $"api/users/{Guid.NewGuid()}/permissions/{created.Id}",
            content: null, TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("User not found.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task GrantPermission_UnknownPermission_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsync(
            $"api/users/{TestUsers.RegularUserId}/permissions/{Guid.NewGuid()}",
            content: null, TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Permission not found.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task GrantPermission_DeactivatedPermission_ReturnsNotFound()
    {
        var (_, permissionId) = await _fixture.AddMcpServerAsync(
            "it-perm-grant-dead", deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsync(
            $"api/users/{TestUsers.RegularUserId}/permissions/{permissionId}",
            content: null, TestContext.Current.CancellationToken);

        var error = await ReadErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Permission not found.", Assert.Single(error.Errors));
    }

    [Fact]
    public async Task CreateUserMe_AdminUser_ReturnsPermissionObjectsIncludingAdministrator()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsync("api/users/me", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(TestUsers.AdminUserId, user.Id);
        var administrator = Assert.Single(user.Permissions, x => x.Id == KnownIds.AdministratorPermissionId);
        Assert.Equal("Administrator", administrator.Name);
    }
}
