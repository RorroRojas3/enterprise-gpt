using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> whose value only changes when a test assigns it.
/// </summary>
internal sealed class StaticOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
{
    public TOptions CurrentValue { get; set; } = value;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
