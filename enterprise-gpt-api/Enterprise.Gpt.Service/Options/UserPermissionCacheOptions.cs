using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

public sealed class UserPermissionCacheOptions
{
    public const string SectionName = "Permissions:Cache";

    /// <summary>
    /// A safety net: grant changes evict at once, but only on the instance that served them, so this is how
    /// long a deactivated administrator keeps access elsewhere. Shorten it before scaling out.
    /// </summary>
    public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class UserPermissionCacheOptionsValidator : AbstractValidator<UserPermissionCacheOptions>
{
    public UserPermissionCacheOptionsValidator()
    {
        // A non-positive lifetime silently degrades the cache into a per-request query.
        RuleFor(options => options.EntryLifetime).GreaterThan(TimeSpan.Zero)
            .WithMessage("Permissions:Cache:EntryLifetime must be greater than zero.");
    }
}
