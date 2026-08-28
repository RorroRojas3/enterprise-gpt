using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the append-only <see cref="ConversationUsageToolCall"/> table and the
/// self-relationship that carries tool nesting.
/// </summary>
public sealed class ConversationUsageToolCallConfiguration : IEntityTypeConfiguration<ConversationUsageToolCall>
{
    public void Configure(EntityTypeBuilder<ConversationUsageToolCall> builder)
    {
        builder.ToTable(nameof(ConversationUsageToolCall), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.ConversationUsage)
            .WithMany(x => x.ToolCalls)
            .HasForeignKey(x => x.ConversationUsageId);

        // The nesting edge. Every row also carries ConversationUsageId, including the nested ones,
        // so a turn's whole tree loads in one query instead of walking the chain.
        builder.HasOne(x => x.Parent)
            .WithMany(x => x.Children)
            .HasForeignKey(x => x.ParentId);

        builder.HasOne(x => x.McpServer)
            .WithMany()
            .HasForeignKey(x => x.McpServerId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        builder.Property(x => x.Kind).HasConversion<int>();

        // Reading one call's tools back in report order.
        builder.HasIndex(x => new { x.ConversationUsageId, x.Sequence });

        // Walking down from a parent, and the lookup the FK constraint itself needs.
        builder.HasIndex(x => x.ParentId);

        // "What did MCP tools cost this month", and the same question per kind.
        builder.HasIndex(x => new { x.Kind, x.DateCreated });

        // Usage reported for one MCP server over a period.
        builder.HasIndex(x => new { x.McpServerId, x.DateCreated });

        // What one agent's model cost over a period, and the per-user ceiling that reads the same rows.
        // Deferred until an agent existed to write a non-null ModelId, which the File Agent now does.
        // Filtered, because SQL Server indexes NULL keys like any other: unfiltered, the largest table
        // in the schema would pay a write per row for an index only agent rows are ever read from.
        builder.HasIndex(x => new { x.ModelId, x.DateCreated })
            .HasFilter("[ModelId] IS NOT NULL");

        // None on Iteration either, deliberately. Attribution groups a single call's rows by it,
        // and the (ConversationUsageId, Sequence) index above already seeks to those rows — the
        // iteration is a filter over a handful of them, not a search key. A reporting query that
        // grouped by iteration across many calls would be the thing that changes this.
    }
}
