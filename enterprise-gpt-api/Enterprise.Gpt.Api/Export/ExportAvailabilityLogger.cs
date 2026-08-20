using Enterprise.Gpt.Service.Export;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Api.Export;

/// <summary>
/// Reports, once at startup, which conversation export formats this deployment can actually serve.
/// </summary>
/// <remarks>
/// <para>
/// A hosted service rather than logging from composition, and that is the whole point of the type.
/// The decisions are made while the container is still being built, before any logging pipeline
/// exists; writing them there means either a throwaway <c>LoggerFactory</c> — which owns a console
/// provider and its thread, and has nothing to dispose it — or output that reaches the console
/// provider only and never Application Insights, which is where an operator debugging a
/// <c>503 /problems/export-renderer-not-configured</c> actually looks.
/// </para>
/// <para>
/// It re-derives the reason rather than being told it: a format that is absent <em>and</em> named in
/// <c>Export:DisabledFormats</c> was withdrawn deliberately, and one that is absent without being
/// named could not be built — which today means PDF with no usable font, or HTML with no template.
/// </para>
/// </remarks>
/// <param name="renderers">The renderers that were registered.</param>
/// <param name="options">The bound export options.</param>
/// <param name="logger">The logger.</param>
internal sealed class ExportAvailabilityLogger(
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

            unavailable.Add(ExportRendererRegistration.IsEnabled(settings, format)
                ? $"{format.ToWireFormat()} (no renderer could be built)"
                : $"{format.ToWireFormat()} (disabled by configuration)");
        }

        _logger.LogInformation(
            "Conversation export is available as {AvailableFormats}.",
            string.Join(", ", available.Select(format => format.ToWireFormat()).Order(StringComparer.Ordinal)));

        if (unavailable.Count > 0)
        {
            // Warning rather than Information: every entry here is a route that answers 503, and an
            // operator who did not intend one wants to see it without going looking. The font case
            // is the one that is usually unintentional — see Export/Fonts/README.md.
            _logger.LogWarning(
                "Conversation export is unavailable as {UnavailableFormats}.", string.Join(", ", unavailable));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
