using Andes.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Enterprise.Gpt.Api.Configuration.Providers;

/// <summary>
/// The middleware settings every chat provider shares, so a second provider cannot drift from the first.
/// </summary>
internal static class ChatClientPipeline
{
    #region Public static methods

    /// <summary>Applies the shared tool-invocation bounds.</summary>
    internal static void ConfigureFunctionInvocation(FunctionInvokingChatClient client)
    {
        client.AllowConcurrentInvocation = false;
        client.IncludeDetailedErrors = true;
        client.MaximumIterationsPerRequest = 5;
        client.MaximumConsecutiveErrorsPerRequest = 5;
    }

    /// <summary>Installs the tool classifiers, so a tool is reported as what it is.</summary>
    /// <remarks>
    /// Both classifiers stack: agents short-circuit and everything else falls through to the MCP one.
    /// Nothing turns tool arguments on, so prompt content, arguments and results stay out of progress events.
    /// </remarks>
    internal static void ConfigureToolTracking(ToolTrackingOptions options)
    {
        options.UseMcpToolClassification();
        options.UseAgentToolClassification();
    }

    #endregion
}
