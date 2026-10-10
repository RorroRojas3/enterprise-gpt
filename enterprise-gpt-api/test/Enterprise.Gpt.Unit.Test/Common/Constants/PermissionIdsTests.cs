using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Enterprise.Gpt.Dto.Enums;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Enums;

/// <summary>
/// Holds the two invariants the built-in permission ids are only useful under.
/// </summary>
public sealed class PermissionIdsTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// <c>PermissionService.EnsurePermissionIsCustom</c> reads <c>Names</c> to decide what an
    /// administrator may not edit, and only ids used in an endpoint filter are forced into it at map
    /// time — so a built-in gated in a service instead would otherwise become renamable unnoticed.
    /// </summary>
    [Fact]
    public void Names_EveryBuiltInId_HasADisplayName()
    {
        var unnamed = BuiltInIds()
            .Where(field => !PermissionIds.Names.ContainsKey((Guid)field.GetValue(null)!))
            .Select(field => field.Name);

        Assert.Empty(unnamed);
    }

    /// <summary>
    /// The 403 an endpoint filter writes and the 400 the built-in guard writes both quote
    /// <c>Names</c>, never the row, so a drift would name something the administrator cannot find.
    /// </summary>
    [Fact]
    public async Task Names_EveryDisplayName_MatchesTheSeededRow()
    {
        var seeded = await _fixture.Context.Permissions
            .AsNoTracking()
            .Where(permission => PermissionIds.Names.Keys.Contains(permission.Id))
            .ToDictionaryAsync(permission => permission.Id, permission => permission.Name, TestContext.Current.CancellationToken);

        Assert.Equal(PermissionIds.Names.Count, seeded.Count);
        Assert.All(PermissionIds.Names, named => Assert.Equal(named.Value, seeded[named.Key]));
    }

    private static IEnumerable<FieldInfo> BuiltInIds() =>
        typeof(PermissionIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Guid));
}
