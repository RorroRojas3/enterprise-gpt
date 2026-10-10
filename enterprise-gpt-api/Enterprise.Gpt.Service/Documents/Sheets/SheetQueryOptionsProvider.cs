using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Serves the spreadsheet lookup's configuration as it stands now.
/// </summary>
/// <remarks>
/// Consumers are scoped, so reading this in a constructor gives one turn a single consistent snapshot
/// and the next turn a fresh one — which is what makes <c>SheetQuery:Enabled</c> a kill switch rather
/// than a redeploy.
/// </remarks>
public interface ISheetQueryOptionsProvider
{
    /// <summary>
    /// Gets the most recently reloaded configuration that validated.
    /// </summary>
    SheetQueryOptions Current { get; }
}

/// <inheritdoc />
/// <remarks>
/// Deliberately not built on <see cref="IOptionsMonitor{TOptions}"/>, which revalidates on the
/// configuration provider's own callback thread where nothing can catch a bad reload; see
/// <c>docs/documents/sheet-query.md</c>.
/// </remarks>
public sealed class SheetQueryOptionsProvider : ISheetQueryOptionsProvider, IDisposable
{
    private readonly IOptionsFactory<SheetQueryOptions> _factory;
    private readonly ILogger<SheetQueryOptionsProvider> _logger;
    private readonly List<IDisposable> _registrations = [];

    private volatile SheetQueryOptions _current;

    /// <summary>
    /// Initializes the provider with the configuration startup validation already accepted.
    /// </summary>
    /// <param name="factory">Builds and validates an instance the way the registered options pipeline does.</param>
    /// <param name="sources">The change tokens the bound configuration section signals a reload through.</param>
    /// <param name="logger">Logger for a reload that had to be ignored.</param>
    public SheetQueryOptionsProvider(
        IOptionsFactory<SheetQueryOptions> factory,
        IEnumerable<IOptionsChangeTokenSource<SheetQueryOptions>> sources,
        ILogger<SheetQueryOptionsProvider> logger)
    {
        _factory = factory;
        _logger = logger;
        _current = factory.Create(Options.DefaultName);

        // Only the unnamed instance: a named one would signal here and rebuild the wrong configuration.
        foreach (var source in sources.Where(s => string.IsNullOrEmpty(s.Name)))
        {
            _registrations.Add(ChangeToken.OnChange(source.GetChangeToken, Reload));
        }
    }

    /// <inheritdoc />
    public SheetQueryOptions Current => _current;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _registrations.Clear();
    }

    private void Reload()
    {
        try
        {
            _current = _factory.Create(Options.DefaultName);
        }
        catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
        {
            _logger.LogError(
                ex,
                "The SheetQuery configuration was reloaded with values that do not validate; the previous configuration stays in effect.");
        }
    }
}
