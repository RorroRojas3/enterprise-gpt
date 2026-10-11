using Enterprise.Gpt.Api.Configuration;
using Enterprise.Gpt.Api.Options;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Exports;
using Enterprise.Gpt.Service.Exports.Interfaces;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Startup;

/// <summary>
/// Reports, once at startup, which conversation export formats this deployment can actually serve.
/// </summary>
/// <remarks>
/// A hosted service because composition runs before the logging pipeline exists, and the operator
/// debugging a <c>503 /problems/export-renderer-not-configured</c> looks in Application Insights.
/// </remarks>
internal sealed partial class ExportAvailabilityLogger(
    IEnumerable<IConversationExportRenderer> renderers,
    IOptions<ExportOptions> options,
    ILogger<ExportAvailabilityLogger> logger) : IHostedService
{
    private readonly IEnumerable<IConversationExportRenderer> _renderers = renderers;
    private readonly IOptions<ExportOptions> _options = options;
    private readonly ILogger<ExportAvailabilityLogger> _logger = logger;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var available = _renderers.Select(renderer => renderer.Format).ToHashSet();
        var settings = _options.Value;

        List<string> unavailable = [];

        foreach (var format in Enum.GetValues<ConversationExportFormats>())
        {
            if (available.Contains(format))
            {
                continue;
            }

            unavailable.Add(ExportsConfiguration.IsEnabled(settings, format)
                ? $"{format.ToWireFormat()} (no renderer could be built)"
                : $"{format.ToWireFormat()} (disabled by configuration)");
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var names = string.Join(", ", available.Select(format => format.ToWireFormat()).Order(StringComparer.Ordinal));
            LogExportFormatsAvailable(_logger, names);
        }

        // Warning: every entry is a route answering 503. The font case is usually unintentional; see
        // Service/Exports/Fonts/README.md.
        if (unavailable.Count > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            var names = string.Join(", ", unavailable);
            LogExportFormatsUnavailable(_logger, names);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation export is available as {AvailableFormats}.")]
    private static partial void LogExportFormatsAvailable(ILogger logger, string availableFormats);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation export is unavailable as {UnavailableFormats}.")]
    private static partial void LogExportFormatsUnavailable(ILogger logger, string unavailableFormats);

    #endregion
}
