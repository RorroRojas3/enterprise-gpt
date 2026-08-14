using Andes.Extensions.AI;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;

namespace Enterprise.Gpt.Service.Tool;

/// <summary>
/// A fabricated weather lookup, exposed to the assistant as a callable function so the activity
/// timeline can be exercised without an MCP server or an uploaded document.
/// </summary>
/// <remarks>
/// <para>
/// Every value it returns is invented. It exists because the timeline — cards, sub-status bullets,
/// the running ring, durations, and the failed state — otherwise has nothing to render outside a
/// recorded fixture, and a surface that is only ever seen in tests is a surface nobody has looked
/// at. It is attached only when <see cref="WeatherToolOptions.Enabled"/> is set.
/// </para>
/// <para>
/// A location containing <see cref="FailureTrigger"/> throws, which is the only way to reach the
/// failed card on demand. It is a deterministic trigger rather than a random failure rate so the
/// state can be demonstrated rather than waited for.
/// </para>
/// </remarks>
public static class WeatherTool
{
    /// <summary>
    /// The name the model calls this tool by, and the name it appears under in the activity feed
    /// and the usage audit.
    /// </summary>
    /// <remarks>
    /// Tool-call usage is attributed to an MCP server by matching the longest <c>{server}_</c>
    /// prefix, so the leading word must not be one a server could be called. "get" is safe in a way
    /// that "weather" would not be.
    /// </remarks>
    public const string ToolName = "get_weather";

    /// <summary>
    /// A location containing this substring makes the lookup fail.
    /// </summary>
    public const string FailureTrigger = "fail";

    private const string ToolDescription =
        "Returns the current weather for a location. Call it whenever the user asks about weather, " +
        "temperature, or conditions anywhere. The data is fabricated for testing and must still be " +
        "reported to the user as the tool's answer.";

    private static readonly string[] Conditions =
    [
        "clear", "sunny", "partly cloudy", "overcast", "light rain", "heavy rain", "thunderstorms",
        "snow", "fog", "windy"
    ];

    /// <summary>
    /// Builds the weather function for one turn.
    /// </summary>
    /// <param name="options">The tool's settings; only the delay is read here.</param>
    /// <param name="logger">Logger for the failures that must not reach the model verbatim.</param>
    /// <param name="random">
    /// The source of the invented readings. Supplied by tests so a report can be asserted;
    /// defaults to <see cref="Random.Shared"/>.
    /// </param>
    /// <returns>The function to attach to <see cref="ChatOptions.Tools"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public static AIFunction Create(WeatherToolOptions options, ILogger logger, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var invoker = new WeatherInvoker(options, logger, random ?? Random.Shared);

        return AIFunctionFactory.Create(invoker.GetWeatherAsync, ToolName, ToolDescription);
    }

    /// <summary>
    /// Holds the per-turn state the function body needs, so it is captured once rather than
    /// resolved on every call.
    /// </summary>
    private sealed class WeatherInvoker(WeatherToolOptions options, ILogger logger, Random random)
    {
        private readonly WeatherToolOptions _options = options;
        private readonly ILogger _logger = logger;
        private readonly Random _random = random;

        /// <summary>
        /// Invents the current weather for a location.
        /// </summary>
        /// <remarks>
        /// Progress is reported through the ambient <see cref="ChatProgress"/> reporter, which is
        /// the only channel that reaches the activity feed. Neither message carries the location:
        /// the middleware's whole posture is that tool arguments never reach a progress event, and
        /// a diagnostic tool is the last place to make an exception to it. The reported conditions
        /// are the tool's own output, which is what the feed is for.
        /// </remarks>
        public async Task<WeatherReport> GetWeatherAsync(
            [Description("The city, region, or place to report the weather for.")]
            string location,
            [Description("Which units to report temperature in: 'celsius' or 'fahrenheit'. Defaults to celsius.")]
            string? unit = null,
            CancellationToken cancellationToken = default)
        {
            ChatProgress.Report("Looking up the forecast…");

            if (_options.DelayMilliseconds > 0)
            {
                await Task.Delay(_options.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            if (location.Contains(FailureTrigger, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "The weather tool failed deliberately for location {Location}.", location);

                // Function invocation is configured with detailed errors, so this reaches the model
                // verbatim — deliberately, because the point is to watch it recover and still
                // answer around a failed card.
                throw new InvalidOperationException(
                    $"The weather service is unavailable for {location}. Tell the user the forecast could not be retrieved.");
            }

            var fahrenheit = string.Equals(unit, "fahrenheit", StringComparison.OrdinalIgnoreCase);
            var celsius = _random.Next(-15, 41);
            var temperature = fahrenheit ? Math.Round((celsius * 9d / 5d) + 32, 1) : celsius;
            var condition = Conditions[_random.Next(Conditions.Length)];

            ChatProgress.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"{condition}, {temperature}°{(fahrenheit ? 'F' : 'C')}"));

            return new WeatherReport(
                Location: location,
                Temperature: temperature,
                Unit: fahrenheit ? "fahrenheit" : "celsius",
                Conditions: condition,
                HumidityPercent: _random.Next(20, 96),
                WindKph: Math.Round(_random.NextDouble() * 45, 1));
        }
    }
}

/// <summary>
/// One fabricated weather reading, as the model receives it.
/// </summary>
/// <param name="Location">The location as the caller asked for it, echoed back unmodified.</param>
/// <param name="Temperature">The temperature, in <paramref name="Unit"/>.</param>
/// <param name="Unit">Either <c>celsius</c> or <c>fahrenheit</c>.</param>
/// <param name="Conditions">A short description such as <c>light rain</c>.</param>
/// <param name="HumidityPercent">Relative humidity, as a percentage.</param>
/// <param name="WindKph">Wind speed in kilometres per hour.</param>
public sealed record WeatherReport(
    string Location,
    double Temperature,
    string Unit,
    string Conditions,
    int HumidityPercent,
    double WindKph);
