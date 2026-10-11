using Enterprise.Gpt.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Enterprise.Gpt.Service.Documents.Sheets;

/// <summary>
/// Serves the spreadsheet lookup's configuration as it stands now.
/// </summary>
/// <remarks>
/// Read it in a scoped constructor: one snapshot per turn is what makes <c>SheetQuery:Enabled</c> a kill
/// switch rather than a redeploy.
/// </remarks>
public interface ISheetQueryOptionsProvider
{
    /// <summary>The most recent reloaded configuration that validated.</summary>
    SheetQueryOptions Current { get; }
}

/// <inheritdoc />
/// <remarks>
/// Not built on <see cref="IOptionsMonitor{TOptions}"/>, which revalidates on the provider's callback thread
/// where a bad reload cannot be caught; see <c>docs/documents/sheet-query.md</c>.
/// </remarks>
public sealed partial class SheetQueryOptionsProvider : ISheetQueryOptionsProvider, IDisposable
{
    private readonly IOptionsFactory<SheetQueryOptions> _factory;
    private readonly ILogger<SheetQueryOptionsProvider> _logger;
    private readonly List<IDisposable> _registrations = [];

    private volatile SheetQueryOptions _current;

    public SheetQueryOptionsProvider(
        IOptionsFactory<SheetQueryOptions> factory,
        IEnumerable<IOptionsChangeTokenSource<SheetQueryOptions>> sources,
        ILogger<SheetQueryOptionsProvider> logger)
    {
        _factory = factory;
        _logger = logger;
        _current = factory.Create(Microsoft.Extensions.Options.Options.DefaultName);

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

    #region Private methods

    private void Reload()
    {
        try
        {
            _current = _factory.Create(Microsoft.Extensions.Options.Options.DefaultName);
        }
        catch (Exception ex) when (ex is OptionsValidationException or InvalidOperationException)
        {
            LogInvalidReload(_logger, ex);
        }
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "The SheetQuery configuration was reloaded with values that do not validate; the previous configuration stays in effect.")]
    private static partial void LogInvalidReload(ILogger logger, Exception exception);

    #endregion
}
