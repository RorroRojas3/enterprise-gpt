using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Enterprise.Gpt.Dto.Actions.Mcp;
using Enterprise.Gpt.Entity;

namespace Enterprise.Gpt.Repository.Configurations
{
    public sealed class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
    {
        public void Configure(EntityTypeBuilder<McpServer> builder)
        {
            builder.ToTable("McpServer", "Core.Ref");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.AuthType)
                .HasConversion<int>();

            // A JSON object in one column rather than a child table: the set is a handful of
            // configuration headers always read and written whole, never queried or joined, and a
            // table would add a join to every projection that already reads the server row.
            //
            // The comparer is stated rather than inferred. EF Core 10 does infer a structural,
            // deep-snapshotting one for a `Dictionary<string, string>` behind a converter, so
            // change detection is not what this buys — ordinal ordering in equality and hashing
            // is, since the default `OrderBy` comparer is culture-sensitive and an EF comparer
            // whose result moves with the ambient culture is a spurious write waiting to happen.
            // It is also the insulation if that inference ever changes.
            //
            // `== null` rather than the house `is null`: these are expression trees, and C#
            // forbids a pattern-matching operation inside one.
            builder.Property(x => x.Headers)
                .HasMaxLength(McpServerHeaderRules.MaxSerializedLength)
                .HasConversion(
                    // A pure round trip with no null-producing arm: EF never passes null to a
                    // value converter — a NULL column is a null property and vice versa — and a
                    // converter that turned a non-null value into null would be the case EF
                    // documents as unsupported. "No headers" is collapsed to null by
                    // `McpServerMapper.ToStoredHeaders`, at the only boundary that builds entities.
                    v => JsonSerializer.Serialize(v, McpServerHeaderRules.SerializerOptions),
                    v => DeserializeHeaders(v),
                    new ValueComparer<Dictionary<string, string>?>(
                        // `StringComparer.Ordinal` on both orderings: the default comparer is
                        // culture-sensitive, and an EF comparer whose result moves with the
                        // ambient culture is a spurious column write waiting to happen.
                        (l, r) => (l == null && r == null)
                            || (l != null && r != null
                                && l.Count == r.Count
                                && l.OrderBy(e => e.Key, StringComparer.Ordinal)
                                    .SequenceEqual(r.OrderBy(e => e.Key, StringComparer.Ordinal))),
                        v => v == null
                            ? 0
                            : v.OrderBy(e => e.Key, StringComparer.Ordinal)
                                .Aggregate(0, (hash, e) => HashCode.Combine(hash, e.Key, e.Value)),
                        // Must copy rather than alias: the snapshot is what an in-place edit of
                        // the live dictionary is compared against.
                        v => v == null
                            ? null
                            : new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase)));

            builder.HasOne(x => x.CreatedBy)
                .WithMany()
                .HasForeignKey(x => x.CreatedById);

            builder.HasOne(x => x.ModifiedBy)
                .WithMany()
                .HasForeignKey(x => x.ModifiedById);

            builder.HasIndex(x => x.Name)
                .HasFilter("[DateDeactivated] IS NULL")
                .IsUnique();
        }

        /// <summary>
        /// Reads the stored JSON back into a case-insensitive dictionary, tolerating anything this
        /// application did not write.
        /// </summary>
        /// <remarks>
        /// Tolerant on purpose. This runs during materialization, which every read of a server
        /// goes through — <c>McpToolProvider.AcquireToolsAsync</c> included — so throwing on one
        /// malformed row would fail tool acquisition for every server in the request and turn
        /// <c>GET api/mcps/all</c> into a 500 an administrator cannot repair through the UI.
        /// Dropping the column is the recoverable failure, and
        /// <c>McpToolProvider.BuildTransportHeaders</c> re-applies the rules to whatever survives.
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

            // Indexer rather than the copy constructor, which throws on keys differing only by
            // case — exactly the hand-written row this is tolerating.
            var result = new Dictionary<string, string>(parsed.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in parsed)
            {
                result[name] = value;
            }

            // A stored `{}` collapses to null like a NULL column does. This application cannot
            // write one — `ToStoredHeaders` is the only writer and it normalizes empty away — but
            // tolerating rows it did not write is this method's whole purpose, and letting one
            // through would put a second representation of "no headers" on the read path.
            return result.Count == 0 ? null : result;
        }
    }
}
