using FluentValidation;

namespace Enterprise.Gpt.Service.Options;

/// <summary>Defaults assume Entra ID access-token lifetimes of 60 to 90 minutes.</summary>
public sealed class McpClientCacheOptions
{
    public const string SectionName = "Mcp:Cache";

    /// <summary>
    /// An entry stops being served this long before its token expires, covering skew and long tool calls.
    /// </summary>
    public TimeSpan TokenExpiryBuffer { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Age cap whatever the token lifetime; the only TTL for unauthenticated servers' shared tool lists.
    /// </summary>
    public TimeSpan MaxEntryLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Tool-call headroom a starting stream is guaranteed. Only cached entries are recreated for it; a fresh
    /// entry is always served, so short-lived tokens cannot cause recreate loops.
    /// </summary>
    public TimeSpan MinRemainingLifetimeForStream { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval of the sweep that disposes expired, unleased entries.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class McpClientCacheOptionsValidator : AbstractValidator<McpClientCacheOptions>
{
    public McpClientCacheOptionsValidator()
    {
        RuleFor(options => options.TokenExpiryBuffer).GreaterThanOrEqualTo(TimeSpan.Zero)
            .WithMessage("Mcp:Cache:TokenExpiryBuffer cannot be negative.");
        RuleFor(options => options.MaxEntryLifetime).GreaterThan(TimeSpan.Zero)
            .WithMessage("Mcp:Cache:MaxEntryLifetime must be greater than zero.");
        RuleFor(options => options.MinRemainingLifetimeForStream).GreaterThanOrEqualTo(TimeSpan.Zero)
            .WithMessage("Mcp:Cache:MinRemainingLifetimeForStream cannot be negative.");
        RuleFor(options => options.ConnectionTimeout).GreaterThan(TimeSpan.Zero)
            .WithMessage("Mcp:Cache:ConnectionTimeout must be greater than zero.");
        RuleFor(options => options.SweepInterval).GreaterThan(TimeSpan.Zero)
            .WithMessage("Mcp:Cache:SweepInterval must be greater than zero.");
    }
}
