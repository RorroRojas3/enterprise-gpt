using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Collection fixture that owns the SQL Server 2025 Testcontainer and the shared
/// <see cref="CustomWebApplicationFactory"/> for the whole integration run. The 2025 image is
/// required because <c>Program.cs</c> pins <c>UseCompatibilityLevel(170)</c>, which older
/// engines reject. Creates the schema with <c>EnsureCreatedAsync</c> (applying the HasData
/// seeds) and adds the fixed <see cref="TestUsers"/> identities, including the admin's
/// <c>Administrator</c> permission.
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2025-latest")
        .Build();

    /// <summary>
    /// Gets the shared application factory. Populated once the container has started.
    /// </summary>
    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EnterpriseGptTests"
        }.ConnectionString;

        Factory = new CustomWebApplicationFactory(connectionString);

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();
        await ctx.Database.EnsureCreatedAsync();
        await SeedTestUsersAsync(ctx);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Restores the model catalog to its post-seed baseline: removes every model except the
    /// seeded one and resets the seeded row's activation/default state. Called per test for
    /// isolation. The seeded model is otherwise treated as read-only by tests — mutating
    /// scenarios must target rows created via <see cref="AddModelAsync"/>, because this reset
    /// does not restore the seeded row's name or limits.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    public async Task ResetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        await ctx.Models
            .Where(x => x.Id != KnownIds.SeedModelId)
            .ExecuteDeleteAsync(cancellationToken);
        await ctx.Models
            .Where(x => x.Id == KnownIds.SeedModelId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.DateDeactivated, (DateTimeOffset?)null)
                .SetProperty(p => p.IsDefault, false), cancellationToken);
    }

    /// <summary>
    /// Inserts a model directly into the database, bypassing the API, for arranging read
    /// scenarios.
    /// </summary>
    /// <param name="name">The model name (also used for display name and description).</param>
    /// <param name="isDefault">Whether the model is the catalog default.</param>
    /// <param name="deactivated">Whether the model is soft-deleted.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The id of the inserted model.</returns>
    public async Task<Guid> AddModelAsync(
        string name, bool isDefault = false, bool deactivated = false, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        var date = DateTimeOffset.UtcNow;
        var model = new Model
        {
            Id = Guid.NewGuid(),
            ProviderId = KnownIds.SeedProviderId,
            Name = name,
            DisplayName = $"{name} display",
            Description = $"{name} description",
            ContextWindowSize = 1000m,
            MaxOutputTokens = 100m,
            IsToolEnabled = true,
            IsDefault = isDefault,
            DateDeactivated = deactivated ? date : null,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        };

        ctx.Models.Add(model);
        await ctx.SaveChangesAsync(cancellationToken);

        return model.Id;
    }

    /// <summary>
    /// Reads a model row straight from the database for state assertions the API does not
    /// expose (e.g. audit columns).
    /// </summary>
    /// <param name="id">The model id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The untracked entity, or <see langword="null"/> when it does not exist.</returns>
    public async Task<Model?> FindModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EnterpriseGptDbContext>();

        return await ctx.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    private static async Task SeedTestUsersAsync(EnterpriseGptDbContext ctx)
    {
        var date = DateTimeOffset.UtcNow;
        ctx.Users.AddRange(
            new User
            {
                Id = TestUsers.AdminUserId,
                FirstName = "Admin",
                LastName = "Tester",
                Email = "admin.tester@example.com",
                DateCreated = date,
                DateModified = date
            },
            new User
            {
                Id = TestUsers.RegularUserId,
                FirstName = "Regular",
                LastName = "Tester",
                Email = "regular.tester@example.com",
                DateCreated = date,
                DateModified = date
            });
        ctx.UserPermissions.Add(new UserPermission
        {
            Id = Guid.NewGuid(),
            UserId = TestUsers.AdminUserId,
            Permission = Permissions.Administrator,
            DateCreated = date,
            DateModified = date,
            CreatedById = TestUsers.AdminUserId,
            ModifiedById = TestUsers.AdminUserId
        });

        await ctx.SaveChangesAsync();
    }
}
