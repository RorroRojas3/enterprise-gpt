using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Adapts the SQL Server-specific parts of the <c>EnterpriseGptDbContext</c> model so it can
/// run on in-memory SQLite:
/// <list type="bullet">
/// <item><description>
/// The rowversion column (<see cref="BaseEntity.Version"/>, mapped via <c>[Timestamp]</c>)
/// becomes an ordinary optional column. SQLite never generates rowversion values, so the
/// store-generated NOT NULL column would otherwise fail every insert — including the HasData
/// seeds applied by <c>EnsureCreated()</c> (see dotnet/efcore#20475). Concurrency-token
/// behavior is exercised against real SQL Server in the integration test project instead.
/// </description></item>
/// <item><description>
/// <see cref="SqlVector{T}"/> columns (SQL Server 2025 vector search) are removed — SQLite
/// has no type mapping for them and model validation would reject the whole model otherwise.
/// </description></item>
/// <item><description>
/// Explicit SQL Server column types (e.g. <c>[Column(TypeName = "nvarchar(max)")]</c>) are
/// stripped so SQLite derives its own store types from the CLR types instead of emitting
/// DDL it cannot parse.
/// </description></item>
/// <item><description>
/// <see cref="DateTimeOffset"/> columns are stored as UTC ticks. The SQLite provider refuses
/// to translate them in <c>ORDER BY</c> and comparison clauses because two rows can carry the
/// same instant under different offsets, so any paged query ordered by <c>DateCreated</c>
/// would throw. Every value the application writes is <c>DateTimeOffset.UtcNow</c>, and
/// <see cref="DateTimeOffset.Equals(DateTimeOffset)"/> compares instants rather than offsets,
/// so the round trip is lossless for these tests.
/// </description></item>
/// </list>
/// </summary>
/// <param name="dependencies">Service dependencies required by the base customizer.</param>
public class SqliteRowVersionModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    private static readonly ValueConverter<DateTimeOffset, long> _dateTimeOffsetConverter =
        new(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> _nullableDateTimeOffsetConverter =
        new(value => value.HasValue ? value.Value.UtcTicks : null,
            ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                // Enough on its own for SQL Server 2025's json type as well as nvarchar(max): the CLR type
                // stays string and SQLite derives TEXT, so a json column needs no removal arm.
                property.SetColumnType(null);

                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(_dateTimeOffsetConverter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(_nullableDateTimeOffsetConverter);
                }
            }

            var version = entityType.FindProperty(nameof(BaseEntity.Version));
            if (version is not null)
            {
                version.ValueGenerated = ValueGenerated.Never;
                version.IsNullable = true;
                version.IsConcurrencyToken = false;
            }

            // SQLite has no type mapping for SqlVector<>, so the CLR property is never added
            // to the model — inspect the CLR type instead and mark it ignored explicitly.
            var vectorPropertyNames = entityType.ClrType
                .GetProperties()
                .Where(p => p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(SqlVector<>))
                .Select(p => p.Name)
                .ToList();
            foreach (var vectorPropertyName in vectorPropertyNames)
            {
                entityType.RemoveProperty(vectorPropertyName);
                entityType.AddIgnored(vectorPropertyName);
            }
        }
    }
}
