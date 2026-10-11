using System.Text.Json;
using Enterprise.Gpt.Common.Constants.McpServers;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Repository.Sql.Configurations.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.McpServers;

public sealed class McpServerConfiguration : BaseModifiedByEntityConfiguration<McpServer, Guid>
{
    /// <inheritdoc />
    /// <remarks>Usage, history and audit columns reach this through required navigations, so a retired row must stay visible.</remarks>
    protected override bool IsSoftDeleteFiltered => false;

    protected override void ConfigureEntity(EntityTypeBuilder<McpServer> builder)
    {
        builder.ToTable(nameof(McpServer), "Core.Ref");

        builder.Property(x => x.Name).HasMaxLength(McpServerLimits.NameMaxLength);
        builder.Property(x => x.Description).HasMaxLength(McpServerLimits.DescriptionMaxLength);
        builder.Property(x => x.Url).HasMaxLength(McpServerLimits.UrlMaxLength);
        builder.Property(x => x.Scope).HasMaxLength(McpServerLimits.ScopeMaxLength);
        builder.Property(x => x.IconKey).HasMaxLength(McpServerLimits.IconKeyMaxLength);

        builder.Property(x => x.AuthType)
            .HasConversion<int>();

        // One JSON column, not a child table: a few headers always read and written whole, never queried.
        // `== null` rather than `is null` because these are expression trees.
        builder.Property(x => x.Headers)
            .HasMaxLength(McpServerHeaderRules.MaxSerializedLength)
            .HasConversion(
                // No null arm: EF never passes null to a converter. `McpServerMapper.ToStoredHeaders`
                // collapses "no headers" to null.
                v => JsonSerializer.Serialize(v, McpServerHeaderRules.SerializerOptions),
                v => DeserializeHeaders(v),
                new ValueComparer<Dictionary<string, string>?>(
                    // Stated rather than inferred for ordinal ordering: a culture-sensitive comparer
                    // causes spurious column writes.
                    (l, r) => (l == null && r == null)
                        || (l != null && r != null
                            && l.Count == r.Count
                            && l.OrderBy(e => e.Key, StringComparer.Ordinal)
                                .SequenceEqual(r.OrderBy(e => e.Key, StringComparer.Ordinal))),
                    v => v == null
                        ? 0
                        : v.OrderBy(e => e.Key, StringComparer.Ordinal)
                            .Aggregate(0, (hash, e) => HashCode.Combine(hash, e.Key, e.Value)),
                    // Copy, not alias: in-place edits of the live dictionary are compared against it.
                    v => v == null
                        ? null
                        : new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase)));

        builder.HasIndex(x => x.Name)
            .HasFilter("[DateDeactivated] IS NULL")
            .IsUnique();
    }

    #region Private methods

    /// <summary>Reads stored headers case-insensitively, tolerating rows this app did not write.</summary>
    /// <remarks>
    /// Runs during materialization, so throwing on one bad row would fail tool acquisition and
    /// <c>GET api/mcps/all</c>; <c>McpToolProvider.BuildTransportHeaders</c> re-applies the rules.
    /// </remarks>
    private static Dictionary<string, string>? DeserializeHeaders(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
                json, McpServerHeaderRules.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        // Indexer, not the copy constructor, which throws on keys differing only by case.
        var result = new Dictionary<string, string>(parsed.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parsed)
        {
            result[name] = value;
        }

        // A stored `{}` collapses to null too, so the read path has one representation of "no headers".
        return result.Count == 0 ? null : result;
    }

    #endregion
}
