using Enterprise.Gpt.Entity.Base;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Adapts the SQL Server-specific parts of the <c>EnterpriseGptDbContext</c> model to in-memory SQLite.
/// </summary>
/// <remarks>
/// Rowversion becomes a plain optional column: SQLite never generates it, so every insert would fail
/// (dotnet/efcore#20475); concurrency is tested against SQL Server instead. Vector columns and explicit
/// column types are dropped because SQLite cannot map them. <see cref="DateTimeOffset"/> is stored as UTC
/// ticks so ordering and comparison translate, lossless because every written value is UTC.
/// </remarks>
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

            var version = entityType.FindProperty(nameof(BaseModifiedEntity<Guid>.Version));
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
