using Enterprise.Gpt.Common.Constants.Documents;
using Enterprise.Gpt.Entity.Base;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Enterprise.Gpt.Repository.Sql.Configurations.Base;

/// <summary>
/// Maps the file columns and owner both document families share.
/// </summary>
public abstract class BaseDocumentConfiguration<T, TKey> : BaseModifiedEntityConfiguration<T, TKey>
    where T : BaseDocument<TKey>
    where TKey : struct, IEquatable<TKey>
{
    /// <inheritdoc />
    protected override void ConfigureBase(EntityTypeBuilder<T> builder)
    {
        base.ConfigureBase(builder);
        builder.Property(e => e.Name).HasMaxLength(DocumentLimits.NameMaxLength);
        builder.Property(e => e.Extension).HasMaxLength(DocumentLimits.ExtensionMaxLength);
        builder.Property(e => e.MimeType).HasMaxLength(DocumentLimits.MimeTypeMaxLength);
        builder.Property(e => e.Path).HasColumnType("nvarchar(max)");
        builder.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
    }
}
