using System.Text.Json;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Tool;

/// <summary>
/// Covers the fabricated weather tool: the shape of what it hands the model, and the deliberate
/// failure that is the only way to reach the activity timeline's failed card on demand.
/// </summary>
public sealed class WeatherToolTests
{
    private static readonly WeatherToolOptions Options = new() { Enabled = true, DelayMilliseconds = 0 };

    private static AIFunction Create(int seed = 1) =>
        WeatherTool.Create(Options, NullLogger.Instance, new Random(seed));

    private static async Task<JsonElement> InvokeAsync(AIFunction function, string location)
    {
        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["location"] = location },
            TestContext.Current.CancellationToken);

        return Assert.IsType<JsonElement>(result);
    }

    [Fact]
    public void Create_NamesTheToolSoUsageCannotBeCreditedToAnMcpServer()
    {
        // Tool-call usage is attributed by longest "{server}_" prefix, so the leading word must not
        // be one an MCP server could plausibly be called.
        Assert.Equal("get_weather", WeatherTool.ToolName);
        Assert.StartsWith("get_", Create().Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ForAnOrdinaryLocation_ReportsThatLocationBack()
    {
        var report = await InvokeAsync(Create(), "Santiago");

        Assert.Equal("Santiago", report.GetProperty("location").GetString());
        Assert.Equal("celsius", report.GetProperty("unit").GetString());
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("conditions").GetString()));
        Assert.InRange(report.GetProperty("humidityPercent").GetInt32(), 20, 95);
    }

    [Fact]
    public async Task InvokeAsync_WithTheSameSeed_ReturnsTheSameReading()
    {
        // The randomness is injected precisely so the invented values are assertable rather than
        // merely plausible.
        var first = await InvokeAsync(Create(seed: 42), "Santiago");
        var second = await InvokeAsync(Create(seed: 42), "Santiago");

        Assert.Equal(first.GetProperty("temperature").GetDouble(), second.GetProperty("temperature").GetDouble());
        Assert.Equal(first.GetProperty("conditions").GetString(), second.GetProperty("conditions").GetString());
    }

    [Fact]
    public async Task InvokeAsync_InFahrenheit_ConvertsAndSaysSo()
    {
        var celsius = await InvokeAsync(Create(seed: 7), "Santiago");

        var result = await Create(seed: 7).InvokeAsync(
            new AIFunctionArguments { ["location"] = "Santiago", ["unit"] = "fahrenheit" },
            TestContext.Current.CancellationToken);
        var fahrenheit = Assert.IsType<JsonElement>(result);

        Assert.Equal("fahrenheit", fahrenheit.GetProperty("unit").GetString());
        Assert.Equal(
            Math.Round((celsius.GetProperty("temperature").GetDouble() * 9d / 5d) + 32, 1),
            fahrenheit.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("failtown")]
    [InlineData("FAILville")]
    [InlineData("South Failbury")]
    public async Task InvokeAsync_ForALocationThatTriggersFailure_Throws(string location)
    {
        // Deterministic rather than a random failure rate: the failed card has to be demonstrable
        // on demand, not waited for.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create().InvokeAsync(
                new AIFunctionArguments { ["location"] = location },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(location, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithoutOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WeatherTool.Create(null!, NullLogger.Instance));
    }

    [Fact]
    public void Create_WithoutALogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WeatherTool.Create(Options, null!));
    }

    // The tool's two progress reports deliberately carry no part of the model's argument — tool
    // arguments never reach a progress event, and a diagnostic tool is the last place to make an
    // exception to that. There is no test for it because `ChatProgress` offers no seam to install a
    // collector: `Report` is a no-op with no ambient tool scope, and a scope needs the tracking
    // middleware around it. The rule is enforced by the constants themselves.
}
