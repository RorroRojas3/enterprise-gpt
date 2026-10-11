namespace Enterprise.Gpt.Integration.Test.TestInfrastructure;

/// <summary>
/// Fixed test identities that <see cref="TestAuthHandler"/> and the database seeding in
/// <see cref="IntegrationTestFixture"/> agree on.
/// </summary>
public static class TestUsers
{
    /// <summary>Seeded with an active <c>Administrator</c> grant.</summary>
    public static readonly Guid AdminUserId = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");

    /// <summary>A non-admin user, for the 403 paths.</summary>
    public static readonly Guid RegularUserId = Guid.Parse("b2b2b2b2-0000-4000-8000-000000000002");
}
