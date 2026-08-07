using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.User;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Integration.Test.TestInfrastructure;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.Endpoints;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserEndpointsIntegrationTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string _knownGuid = "0e984725-c51c-4bf5-9960-e1c80e27aba0";

    private readonly IntegrationTestFixture _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetUsersAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resets on the way out as well as in. Unlike the other endpoint suites, these tests mutate
    /// the shared <see cref="TestUsers"/> identities themselves — deactivating them and rewriting
    /// their profiles — and a left-behind soft delete makes every later grant in the collection
    /// fail with "User not found."
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _fixture.ResetUsersAsync(TestContext.Current.CancellationToken);
    }

    private static UpdateUserActionDto UpdateRequest(params Guid[] permissionIds)
    {
        return new UpdateUserActionDto
        {
            FirstName = "Updated",
            LastName = "Tester",
            Email = "updated.tester@example.com",
            PermissionIds = permissionIds
        };
    }


    /// <summary>
    /// Creates a client for an identity that has no database row yet, so <c>me</c> takes the
    /// provisioning path. <see cref="TestAuthHandler"/> accepts a raw GUID as the user header.
    /// </summary>
    private HttpClient CreateNewIdentityClient(Guid oid)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, oid.ToString());
        return client;
    }

    [Theory]
    [InlineData("POST", "api/users/me")]
    [InlineData("GET", "api/users")]
    [InlineData("GET", $"api/users/{_knownGuid}")]
    [InlineData("POST", "api/users")]
    [InlineData("PUT", $"api/users/{_knownGuid}")]
    [InlineData("DELETE", $"api/users/{_knownGuid}")]
    public async Task UserRoutes_AnonymousUser_ReturnsUnauthorized(string method, string route)
    {
        using var client = _fixture.Factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Unauthorized", problem.Title);
    }

    [Theory]
    [InlineData("GET", "api/users")]
    [InlineData("GET", $"api/users/{_knownGuid}")]
    [InlineData("POST", "api/users")]
    [InlineData("PUT", $"api/users/{_knownGuid}")]
    [InlineData("DELETE", $"api/users/{_knownGuid}")]
    public async Task AdminUserRoutes_RegularUser_ReturnsForbidden(string method, string route)
    {
        using var client = _fixture.Factory.CreateUserClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT")
        {
            // A well-formed body so the request reaches the admin filter instead of failing
            // JSON binding first.
            request.Content = method is "POST"
                ? JsonContent.Create(new CreateUserActionDto { Email = "forbidden@example.com" })
                : JsonContent.Create(UpdateRequest());
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Administrator permission is required.", problem.Detail);
    }

    [Fact]
    public async Task CreateCurrentUser_ExistingUser_ReturnsOkWithProfile()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(TestUsers.AdminUserId, user.Id);
        Assert.Equal("Admin Tester", user.FullName);
        Assert.Contains(user.Permissions, p => p.Id == KnownIds.AdministratorPermissionId);
        // Signing in reconciles the seeded Upload File default, which existing users never received at
        // provisioning time.
        Assert.Contains(user.Permissions, p => p.Id == PermissionIds.UploadFile);
    }

    [Fact]
    public async Task CreateCurrentUser_NewIdentity_ReturnsCreatedWithDefaultPermissionsOnly()
    {
        var defaultPermissionId = await _fixture.AddPermissionAsync(
            "it-user-default", isDefault: true, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddPermissionAsync("it-user-specialized", cancellationToken: TestContext.Current.CancellationToken);
        var oid = Guid.NewGuid();
        _fixture.Factory.GraphService.AddUser(oid, "Ada", "Lovelace", "ada.lovelace@example.com");
        using var client = CreateNewIdentityClient(oid);

        var response = await client.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/users/{oid}", response.Headers.Location?.ToString());
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(oid, user.Id);
        Assert.Equal("Ada", user.FirstName);
        Assert.Equal("ada.lovelace@example.com", user.Email);
        // The seeded built-in Upload File permission is itself a default, so it joins the one this test
        // adds; the specialized permission must not.
        Assert.Equal(
            new[] { defaultPermissionId, PermissionIds.UploadFile }.Order(),
            user.Permissions.Select(p => p.Id).Order());
        Assert.All(user.Permissions, p => Assert.True(p.IsDefault));

        var persisted = await _fixture.FindUserAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(
            new[] { defaultPermissionId, PermissionIds.UploadFile }.Order(),
            persisted.UserPermissions.Select(p => p.PermissionId).Order());
    }

    [Fact]
    public async Task CreateCurrentUser_CalledTwice_IsIdempotentAndReturnsOkOnRepeat()
    {
        var oid = Guid.NewGuid();
        _fixture.Factory.GraphService.AddUser(oid, "Ada", "Lovelace", "ada.lovelace@example.com");
        using var client = CreateNewIdentityClient(oid);

        var first = await client.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);
        var second = await client.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var user = await second.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(oid, user.Id);
    }

    [Fact]
    public async Task CreateCurrentUser_DeactivatedUser_ReturnsForbidden()
    {
        _fixture.Factory.GraphService.AddUser(
            TestUsers.RegularUserId, "Regular", "Tester", "regular.tester@example.com");
        using var adminClient = _fixture.Factory.CreateAdminClient();
        var deleteResponse = await adminClient.DeleteAsync(
            $"api/users/{TestUsers.RegularUserId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var userClient = _fixture.Factory.CreateUserClient();
        var response = await userClient.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("This account has been deactivated.", problem.Detail);
    }

    [Fact]
    public async Task RevokePermission_AdminRevokingOwnAdministrator_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"api/users/{TestUsers.AdminUserId}/permissions/{KnownIds.AdministratorPermissionId}",
            TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains("Administrator", Assert.Single(problem.Errors["PermissionId"]), StringComparison.Ordinal);

        // The grant survived, so the admin routes still work.
        var stillAdmin = await client.GetAsync("api/users", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillAdmin.StatusCode);
    }

    [Fact]
    public async Task CreateCurrentUser_IdentityMissingFromDirectory_ReturnsNotFound()
    {
        using var client = CreateNewIdentityClient(Guid.NewGuid());

        var response = await client.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCurrentUser_PreCreatedByAdmin_ReturnsAdministratorAssignedPermissions()
    {
        var permissionId = await _fixture.AddPermissionAsync(
            "it-user-assigned", cancellationToken: TestContext.Current.CancellationToken);
        var oid = Guid.NewGuid();
        _fixture.Factory.GraphService.AddUser(oid, "Grace", "Hopper", "grace.hopper@example.com");
        using var adminClient = _fixture.Factory.CreateAdminClient();
        var createResponse = await adminClient.PostAsJsonAsync("api/users", new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permissionId]
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var userClient = CreateNewIdentityClient(oid);
        var response = await userClient.PostAsync("api/users/me", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(oid, user.Id);
        Assert.Contains(user.Permissions, p => p.Id == permissionId);
    }

    [Fact]
    public async Task CreateUser_AdminUser_ReturnsCreatedAndPersistsAudit()
    {
        var permissionId = await _fixture.AddPermissionAsync(
            "it-user-requested", cancellationToken: TestContext.Current.CancellationToken);
        var defaultPermissionId = await _fixture.AddPermissionAsync(
            "it-user-default", isDefault: true, cancellationToken: TestContext.Current.CancellationToken);
        var oid = Guid.NewGuid();
        _fixture.Factory.GraphService.AddUser(oid, "Grace", "Hopper", "grace.hopper@example.com");
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [permissionId]
        };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/users", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(created);
        Assert.Equal(oid, created.Id);
        Assert.Equal("Grace Hopper", created.FullName);
        Assert.Equal(
            new[] { permissionId, defaultPermissionId, PermissionIds.UploadFile }.Order(),
            created.Permissions.Select(p => p.Id).Order());

        var persisted = await _fixture.FindUserAsync(oid, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.All(persisted.UserPermissions, x => Assert.Equal(TestUsers.AdminUserId, x.CreatedById));
    }

    [Fact]
    public async Task CreateUser_EmailMissingFromDirectory_ReturnsNotFound()
    {
        var request = new CreateUserActionDto { Email = "nobody@example.com" };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/users", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateUser_ExistingUser_ReturnsBadRequest()
    {
        _fixture.Factory.GraphService.AddUser(TestUsers.RegularUserId, "Regular", "Tester", "regular.tester@example.com");
        var request = new CreateUserActionDto { Email = "regular.tester@example.com" };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/users", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task CreateUser_InvalidEmail_ReturnsBadRequest()
    {
        var request = new CreateUserActionDto { Email = "not-an-email" };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/users", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task CreateUser_UnknownPermission_ReturnsNotFound()
    {
        _fixture.Factory.GraphService.AddUser(Guid.NewGuid(), "Grace", "Hopper", "grace.hopper@example.com");
        var request = new CreateUserActionDto
        {
            Email = "grace.hopper@example.com",
            PermissionIds = [Guid.NewGuid()]
        };
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("api/users", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUsers_AdminUser_ReturnsPagedActiveUsers()
    {
        await _fixture.AddUserAsync("Ada", "Lovelace", "ada.lovelace@example.com",
            cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserAsync("Deleted", "Person", "deleted.person@example.com",
            deactivated: true, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync("api/users?name=Lovelace", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponseDto<UserDto>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ada.lovelace@example.com", Assert.Single(page.Items).Email);
    }

    [Fact]
    public async Task GetUsers_NoQueryArguments_UsesDefaultPaging()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync("api/users", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponseDto<UserDto>>(TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(1, page.CurrentPage);
        Assert.Contains(page.Items, x => x.Id == TestUsers.AdminUserId);
    }

    [Fact]
    public async Task GetUser_AdminUser_ReturnsUserWithActiveGrants()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync($"api/users/{TestUsers.AdminUserId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal(KnownIds.AdministratorPermissionId, Assert.Single(user.Permissions).Id);
    }

    [Fact]
    public async Task GetUser_UnknownUser_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.GetAsync($"api/users/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateUser_AdminUser_ReplacesProfileAndPermissionSet()
    {
        var keepId = await _fixture.AddPermissionAsync("it-user-keep", cancellationToken: TestContext.Current.CancellationToken);
        var dropId = await _fixture.AddPermissionAsync("it-user-drop", cancellationToken: TestContext.Current.CancellationToken);
        var addId = await _fixture.AddPermissionAsync("it-user-add", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, keepId, cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(TestUsers.RegularUserId, dropId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/users/{TestUsers.RegularUserId}", UpdateRequest(keepId, addId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        Assert.Equal("Updated Tester", user.FullName);
        Assert.Equal("updated.tester@example.com", user.Email);
        Assert.Equal(
            new[] { keepId, addId }.OrderBy(x => x),
            user.Permissions.Select(p => p.Id).OrderBy(x => x));

        var dropGrants = await _fixture.FindUserPermissionsAsync(
            TestUsers.RegularUserId, dropId, TestContext.Current.CancellationToken);
        Assert.NotNull(Assert.Single(dropGrants).DateDeactivated);
    }

    [Fact]
    public async Task UpdateUser_AdminRevokingOwnAdministrator_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/users/{TestUsers.AdminUserId}", UpdateRequest(), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.ReadValidationAsync(response);
        Assert.Contains("Administrator", Assert.Single(problem.Errors["PermissionIds"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateUser_UnknownUser_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.PutAsJsonAsync(
            $"api/users/{Guid.NewGuid()}", UpdateRequest(), TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateUser_InvalidRequest_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var request = new UpdateUserActionDto
        {
            FirstName = "",
            LastName = "Tester",
            Email = "not-an-email"
        };

        var response = await client.PutAsJsonAsync(
            $"api/users/{TestUsers.RegularUserId}", request, TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task DeactivateUser_AdminUser_ReturnsNoContentAndCascadesGrants()
    {
        var permissionId = await _fixture.AddPermissionAsync(
            "it-user-cascade", cancellationToken: TestContext.Current.CancellationToken);
        await _fixture.AddUserPermissionAsync(
            TestUsers.RegularUserId, permissionId, cancellationToken: TestContext.Current.CancellationToken);
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"api/users/{TestUsers.RegularUserId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var persisted = await _fixture.FindUserAsync(TestUsers.RegularUserId, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.DateDeactivated);
        Assert.All(persisted.UserPermissions, x => Assert.NotNull(x.DateDeactivated));
    }

    [Fact]
    public async Task DeactivateUser_AlreadyDeactivated_ReturnsNotFound()
    {
        using var client = _fixture.Factory.CreateAdminClient();
        var first = await client.DeleteAsync(
            $"api/users/{TestUsers.RegularUserId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.DeleteAsync(
            $"api/users/{TestUsers.RegularUserId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(second, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivateUser_AdminDeactivatingThemselves_ReturnsBadRequest()
    {
        using var client = _fixture.Factory.CreateAdminClient();

        var response = await client.DeleteAsync(
            $"api/users/{TestUsers.AdminUserId}", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadValidationAsync(response);
    }

    [Fact]
    public async Task DeactivateUser_DeactivatedAdministrator_LosesAdminAccess()
    {
        var oid = Guid.NewGuid();
        _fixture.Factory.GraphService.AddUser(oid, "Second", "Admin", "second.admin@example.com");
        using var adminClient = _fixture.Factory.CreateAdminClient();
        var createResponse = await adminClient.PostAsJsonAsync("api/users", new CreateUserActionDto
        {
            Email = "second.admin@example.com",
            PermissionIds = [KnownIds.AdministratorPermissionId]
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var secondAdminClient = CreateNewIdentityClient(oid);
        var beforeDeactivation = await secondAdminClient.GetAsync("api/users", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, beforeDeactivation.StatusCode);

        var deleteResponse = await adminClient.DeleteAsync($"api/users/{oid}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeactivation = await secondAdminClient.GetAsync("api/users", TestContext.Current.CancellationToken);

        await ProblemAssert.ReadAsync(afterDeactivation, HttpStatusCode.Forbidden);
    }
}
