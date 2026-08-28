using System.ComponentModel.DataAnnotations;

namespace Enterprise.Gpt.Service.Settings;

/// <summary>
/// Settings for the File Agent, bound from the <c>FileAgent</c> configuration section and validated
/// at startup.
/// </summary>
/// <remarks>
/// <para>
/// The section carries the agent's catalog row id and nothing else <em>about the model</em>: its
/// provider, its deployment name and its prices are read from that <c>Core.Ref.Model</c> row at the
/// moment of use, so a fact about the agent's model can never be stated in two places that disagree.
/// </para>
/// <para>
/// Everything else here bounds the <em>run</em> rather than describing the model. None of it is
/// derived from a measured baseline, so all of it is bindable — retuning is a configuration change,
/// never a redeploy.
/// </para>
/// </remarks>
public sealed class FileAgentOptions
{
    /// <summary>
    /// The configuration section this type binds from.
    /// </summary>
    public const string SectionName = "FileAgent";

    /// <summary>
    /// Gets or sets whether the File Agent is offered to the model at all.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The feature's whole rollback: with it off the tool is not attached to any turn, so the model
    /// cannot call it and no sandbox session is ever started. Files already generated stay
    /// downloadable — this governs new generation, not access to what it produced. Defaults off
    /// everywhere, development included, because a capability billed per sandbox run should be
    /// opted into rather than discovered. Startup validation runs regardless of this flag.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the <c>Core.Ref.Model</c> identifier of the model the agent runs on.
    /// </summary>
    /// <remarks>
    /// Pinned rather than following the conversation's own selected model, and it must resolve to an
    /// Azure OpenAI row: the hosted code interpreter rides the Responses route, and the other three
    /// providers structurally cannot carry it.
    /// </remarks>
    public Guid ModelId { get; set; }

    /// <summary>
    /// Gets or sets the wall-clock ceiling on one <c>file_agent</c> invocation, in seconds.
    /// Default: <c>300</c>.
    /// </summary>
    /// <remarks>
    /// Bounds the whole invocation — every sandbox round trip inside it included — independently of
    /// the outer turn's own function-invocation bounds, which are a different client's settings and
    /// are not inherited. A run holds the conversation lock, so this is also what bounds how long a
    /// second turn waits on 409.
    /// </remarks>
    [Range(30, 1800)]
    public int ToolTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets how many artifacts one run may return.
    /// Default: <c>3</c>.
    /// </summary>
    /// <remarks>
    /// A script that writes in a loop would otherwise persist an unbounded number of blobs. Over the
    /// ceiling the surplus is dropped and the run says so, rather than failing a run that did produce
    /// what was asked for.
    /// </remarks>
    [Range(1, 10)]
    public int MaxArtifactsPerRun { get; set; } = 3;

    /// <summary>
    /// Gets or sets how many function-invocation iterations one agent run may take.
    /// Default: <c>12</c>.
    /// </summary>
    /// <remarks>
    /// Its own number rather than the turn's, because the agent spends iterations the turn does not:
    /// loading a skill, reading its reference, running the code, and re-opening the artifact to check
    /// it. The turn's own ceiling bounds how many tools the assistant may call, which is a different
    /// question, and neither is inherited from the other.
    /// </remarks>
    [Range(2, 40)]
    public int MaxIterationsPerRun { get; set; } = 12;
}
