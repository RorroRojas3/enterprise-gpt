using Enterprise.Gpt.Service.Settings;
using Microsoft.Extensions.Configuration;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Resolves the settings the capability spike needs, and decides whether it may run at all.
/// </summary>
/// <remarks>
/// Each run bills a Code Interpreter container per minute with a five-minute minimum, so credentials
/// alone are deliberately not the trigger — a developer box running the API already has them, and
/// <see cref="IsEnabled"/> requires an explicit opt-in key on top.
/// </remarks>
internal static class SpikeConfiguration
{
    /// <summary>Why the spike skips, phrased as the command that turns it on.</summary>
    /// <remarks>A skip, not a failure: no credentials is the normal state of CI.</remarks>
    public const string SkipReason =
        "The File Agent capability spike is opt-in because each run bills a Code Interpreter "
        + "session. Enable it with: dotnet user-secrets --id " + ApiUserSecretsId
        + " set \"FileAgentSpike:Enabled\" \"true\" — and make sure AzureOpenAI:Url, AzureOpenAI:ApiKey "
        + "and AzureOpenAI:DefaultModel are set in the same store. See docs/file-agent/sandbox-capabilities.md.";

    /// <summary>Why the benchmark skips, phrased as the command that turns it on.</summary>
    /// <remarks>
    /// Its own key rather than the spike's: the benchmark is thirty billable runs where the spike is a
    /// handful, and opting into one must not opt into the other.
    /// </remarks>
    public const string BenchmarkSkipReason =
        "The File Agent benchmark is opt-in because it bills thirty Code Interpreter sessions. "
        + "Enable it with: dotnet user-secrets --id " + ApiUserSecretsId
        + " set \"FileAgentBenchmark:Enabled\" \"true\" — and make sure AzureOpenAI:Url, AzureOpenAI:ApiKey "
        + "and AzureOpenAI:DefaultModel are set in the same store. See docs/file-agent/the-agent.md.";

    public const string SectionName = "FileAgentSpike";

    /// <summary>The section the benchmark's own opt-in lives under.</summary>
    public const string BenchmarkSectionName = "FileAgentBenchmark";

    // Literal copy of Enterprise.Gpt.Api.csproj's UserSecretsId: AddUserSecrets<T> resolves the id
    // from the assembly it is given, which for this one is no id at all.
    private const string ApiUserSecretsId = "27ef94e5-8a02-48e5-b431-caa1880f8198";

    private static readonly IConfiguration Configuration = Build();

    private static readonly AzureOpenAIOptions AzureOpenAI = Bind();

    // Every read here happens inside Assert.SkipUnless, where a throw is a failed test rather than a
    // skipped one - and this suite's whole contract is that it stays green on a machine that has not
    // opted in. So an unreadable configuration degrades to "not configured": GetSecretsPathFromSecretsId
    // throws when neither APPDATA nor HOME is set, and GetValue<bool> throws on a value like "1".
    private static IConfiguration Build()
    {
        try
        {
            return new ConfigurationBuilder()
                .AddUserSecrets(ApiUserSecretsId)
                .AddEnvironmentVariables()
                .Build();
        }
        catch (Exception)
        {
            return new ConfigurationBuilder().Build();
        }
    }

    private static AzureOpenAIOptions Bind()
    {
        try
        {
            return Configuration.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>() ?? new();
        }
        catch (Exception)
        {
            return new AzureOpenAIOptions();
        }
    }

    private static bool Flag(string key, string section = SectionName)
    {
        try
        {
            return Configuration.GetValue<bool>($"{section}:{key}");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Gets whether the spike may make billable calls: opted in <em>and</em> configured.</summary>
    public static bool IsEnabled =>
        Flag("Enabled")
        && AzureOpenAI.IsUrlAbsolute
        && AzureOpenAI.IsUrlResourceRoot
        && !string.IsNullOrWhiteSpace(AzureOpenAI.ApiKey)
        && !string.IsNullOrWhiteSpace(DeploymentName);

    /// <summary>Gets whether the thirty-prompt benchmark may make its billable calls.</summary>
    public static bool IsBenchmarkEnabled =>
        Flag("Enabled", BenchmarkSectionName)
        && AzureOpenAI.IsUrlAbsolute
        && AzureOpenAI.IsUrlResourceRoot
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(DeploymentName);

    /// <summary>Gets whether a run overwrites the committed evidence instead of asserting against it.</summary>
    /// <remarks>Off by default: a run that silently rewrote the fixture it was meant to diff would prove nothing.</remarks>
    public static bool IsRecording => Flag("Record");

    /// <summary>Gets the OpenAI-compatible v1 endpoint, derived exactly as <c>Program.cs</c> derives it.</summary>
    public static Uri Endpoint => AzureOpenAI.V1Endpoint;

    public static string ApiKey => AzureOpenAI.ApiKey;

    /// <summary>
    /// Gets the deployment the spike sends its requests to: <c>AzureOpenAI:DefaultModel</c> unless
    /// <c>FileAgentSpike:Model</c> pins one of its own.
    /// </summary>
    public static string DeploymentName =>
        Configuration.GetValue<string>($"{SectionName}:Model") is { Length: > 0 } model
            ? model
            : AzureOpenAI.DefaultModel;
}
