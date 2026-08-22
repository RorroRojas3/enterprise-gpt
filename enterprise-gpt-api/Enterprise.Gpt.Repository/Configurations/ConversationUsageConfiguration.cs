using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations;

/// <summary>
/// Configures the append-only <see cref="ConversationUsage"/> audit table.
/// </summary>
public sealed class ConversationUsageConfiguration : IEntityTypeConfiguration<ConversationUsage>
{
    public void Configure(EntityTypeBuilder<ConversationUsage> builder)
    {
        builder.ToTable(nameof(ConversationUsage), "Core");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Usage)
            .HasForeignKey(x => x.ConversationId);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId);

        builder.HasOne(x => x.Provider)
            .WithMany()
            .HasForeignKey(x => x.ProviderId);

        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();

        // Serves both the per-conversation history and the "newest turn" lookup that restores a
        // conversation's MCP selection. Kind is in the key because that lookup filters on it —
        // without it the engine scans backwards through however many naming rows sit at the tail.
        builder.HasIndex(x => new { x.ConversationId, x.Kind, x.DateCreated });

        // Usage reported for one user over a period.
        builder.HasIndex(x => new { x.UserId, x.DateCreated });

        // Usage reported for one model over a period.
        builder.HasIndex(x => new { x.ModelId, x.DateCreated });

        // The reporting scan behind GET api/reports/usage. The three indexes above lead with a
        // dimension because they answer "this user's history" and "this model's history"; a report
        // asks the opposite question — one window, every dimension at once — so none of them is
        // seekable for it and a grouping by day has nothing date-leading at all.
        //
        // The included columns are every column the aggregate reads, which makes the totals, the
        // day series and the two distinct counts covering seeks. The model, provider and user
        // groupings still join the catalog for their labels, so those plans need the join either
        // way — the index removes their row lookups, not the join.
        //
        // The included set is close to the whole row, so this roughly doubles the table's write
        // cost, on the fastest-growing table in the schema. It is paid for by the alternative:
        // without it every report either scans the clustered index or reads one of the composites
        // above end to end, on a table nothing prunes. A nonclustered columnstore index is the
        // other answer to this shape of workload on SQL Server 2025 and would compress far better;
        // it is not taken here because it would be the only columnstore index in the schema and
        // its delta-store behaviour under this table's steady trickle of single-row inserts has
        // not been measured.
        //
        // Building it is an offline operation. See the migration for what that costs on a table
        // that has been accumulating since the schema was created.
        builder.HasIndex(x => x.DateCreated)
            .IncludeProperties(x => new
            {
                x.UserId,
                x.ConversationId,
                x.ModelId,
                x.ProviderId,
                x.Kind,
                x.Status,
                x.InputTokens,
                x.OutputTokens,
                x.ToolInputTokens,
                x.ToolOutputTokens,
                x.ContextTokens,
                x.InputPricePerMillionTokens,
                x.OutputPricePerMillionTokens
            });
    }
}
