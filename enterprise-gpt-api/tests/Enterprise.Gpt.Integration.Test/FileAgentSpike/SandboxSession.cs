using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// One conversation with the hosted Code Interpreter, over the same Responses-route client
/// <c>Program.cs</c> registers under <c>ChatClientKeys.AzureOpenAI</c>.
/// </summary>
/// <remarks>
/// No function invocation, tool tracking or options decorator: a failure inside middleware would be
/// indistinguishable from a failure in the hosted tool itself. Turns accumulate whole, because the
/// container is reused only while a prior code interpreter call stays in context, and dropping a user
/// message while keeping the tool result that answered it would orphan that result.
/// </remarks>
// Class-scope suppression, wider than the four call sites, to keep the experimental surface
// (Microsoft.Extensions.AI.OpenAI 10.9.0, OpenAI 2.12.0) greppable to this file and ArtifactExtractor.
#pragma warning disable OPENAI001, MEAI001
internal sealed class SandboxSession : IAsyncDisposable
{
    public const string PlainTextMediaType = "text/plain";

    private const string UploadPurpose = "assistants";

    // Tagged fence, not bare: an untagged one comes back rewritten markedly more often.
    private const string OpeningFence = "```python";
    private const string ClosingFence = "```";

    private const string SystemInstruction =
        """
        You are driving a capability probe. Every request gives you a complete Python program.

        Run it with the code interpreter exactly as written. Do not rewrite it, do not shorten it,
        do not add to it, and do not substitute a library it does not import. If it raises, report
        the traceback and stop; do not repair it and re-run.

        The program writes its own results to a file. Never restate those results in prose, never
        summarise them, and never claim an outcome the program did not produce.
        """;

    private readonly IChatClient _chat;
    private readonly IHostedFileClient _files;
    private readonly HostedCodeInterpreterTool _codeInterpreter;
    private readonly List<List<ChatMessage>> _turns = [];
    private readonly List<AIContent> _inputs = [];
    private readonly List<string> _uploadedFileIds = [];
    private readonly int _maxTurns;

    private SandboxSession(IChatClient chat, IHostedFileClient files, int maxTurns)
    {
        _chat = chat;
        _files = files;
        _codeInterpreter = new HostedCodeInterpreterTool { Inputs = _inputs };
        _maxTurns = maxTurns;
    }

    public string DeploymentName { get; } = SpikeConfiguration.DeploymentName;

    public string? ContainerId { get; private set; }

    /// <param name="maxTurns">
    /// Whole exchanges to keep, or zero to keep all. Bound it across independent scripts: the
    /// container survives on a recent tool call, and older code only costs input tokens.
    /// </param>
    public static SandboxSession Create(int maxTurns = 0)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(SpikeConfiguration.ApiKey),
            new OpenAIClientOptions { Endpoint = SpikeConfiguration.Endpoint });

        var chat = client.GetResponsesClient().AsIChatClient(SpikeConfiguration.DeploymentName);
        var files = client.AsIHostedFileClient();

        return new SandboxSession(chat, files, maxTurns);
    }

    /// <summary>
    /// Uploads bytes to the Files API and returns the reference the sandbox can be given.
    /// </summary>
    /// <remarks>
    /// A file reaches the container as a file id and nothing else; raw bytes are dropped silently —
    /// see <c>Inputs_SuppliedAsDataContent_AreSilentlyDropped</c>.
    /// </remarks>
    public async Task<HostedFileContent> UploadAsync(
        string fileName, string mediaType, byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);

        var uploaded = await _files.UploadAsync(
            stream, mediaType, fileName, new HostedFileClientOptions { Purpose = UploadPurpose }, cancellationToken);

        _uploadedFileIds.Add(uploaded.FileId);

        return uploaded;
    }

    /// <summary>Replaces the sandbox's inputs for the next run; no arguments clears them.</summary>
    public void SetInputs(params AIContent[] inputs)
    {
        _inputs.Clear();
        _inputs.AddRange(inputs);
    }

    /// <summary>Runs one complete Python program in the sandbox, verbatim.</summary>
    public Task<SandboxRun> RunPythonAsync(string script, CancellationToken cancellationToken) =>
        RunAsync(
            $"Run this program:{Environment.NewLine}{OpeningFence}{Environment.NewLine}{script}{Environment.NewLine}{ClosingFence}",
            cancellationToken);

    /// <summary>Sends one instruction and folds the reply into the conversation.</summary>
    public async Task<SandboxRun> RunAsync(string instruction, CancellationToken cancellationToken)
    {
        List<ChatMessage> turn = [new(ChatRole.User, instruction)];
        List<ChatMessage> request = [new(ChatRole.System, SystemInstruction), .. _turns.SelectMany(kept => kept), .. turn];

        var response = await _chat.GetResponseAsync(
            request,
            new ChatOptions { ModelId = DeploymentName, Tools = [_codeInterpreter] },
            cancellationToken);

        turn.AddRange(response.Messages);
        _turns.Add(turn);
        Trim();

        var run = ArtifactExtractor.Extract(response);

        // Latest wins: a container is provisioned per response and recycled once it falls out of
        // context, so a pinned first id would have DownloadAsync 404 against a dead container.
        ContainerId = run.ContainerId ?? ContainerId;

        return run;
    }

    /// <summary>Reads a file out of the container by name, cited or not.</summary>
    /// <remarks>
    /// The citation annotations name only the files the model chose to mention, which is not the set
    /// a program wrote. Listing the container is what makes a probe's own output readable regardless.
    /// </remarks>
    public async Task<byte[]?> TryReadContainerFileAsync(string fileName, CancellationToken cancellationToken)
    {
        if (ContainerId is not { Length: > 0 } container)
        {
            return null;
        }

        var options = new HostedFileClientOptions { Scope = container };

        await foreach (var file in _files.ListFilesAsync(options, cancellationToken))
        {
            if (!string.Equals(Path.GetFileName(file.Name), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = await _files.DownloadAsync(file.FileId, options, cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            return buffer.ToArray();
        }

        return null;
    }

    /// <summary>Downloads an artifact the sandbox produced.</summary>
    /// <exception cref="InvalidOperationException">The artifact carries no downloadable identity.</exception>
    public async Task<byte[]> DownloadAsync(SandboxArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.InlineData is { } inline)
        {
            return inline;
        }

        if (artifact.FileId is not { Length: > 0 } fileId)
        {
            throw new InvalidOperationException(
                $"The artifact found on '{artifact.Channel}' carries neither bytes nor a file id, so it cannot be read back.");
        }

        // Without the container scope this resolves against the standard Files API, which cannot
        // see a file the code interpreter wrote, and answers 404.
        var scope = artifact.ContainerId ?? ContainerId;

        using var stream = await _files.DownloadAsync(
            fileId,
            scope is { Length: > 0 } ? new HostedFileClientOptions { Scope = scope } : null,
            cancellationToken);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }

    /// <summary>Deletes every file this session uploaded. Idempotent.</summary>
    /// <returns>The files that survived, each with the failure that spared it. Empty on success.</returns>
    /// <remarks>
    /// Best effort: a leak costs file quota and must not fail a probe. Returned rather than swallowed
    /// because this harness is the only uploader, so a silent leak has no other symptom.
    /// </remarks>
    internal async Task<IReadOnlyList<string>> ReleaseUploadsAsync()
    {
        List<string> leaked = [];

        foreach (var fileId in _uploadedFileIds)
        {
            try
            {
                await _files.DeleteAsync(fileId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                leaked.Add($"{fileId} ({exception.GetType().Name})");
            }
        }

        _uploadedFileIds.Clear();

        return leaked;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var leaked = await ReleaseUploadsAsync().ConfigureAwait(false);

        if (leaked.Count > 0)
        {
            // Last resort only — this repository does not switch runner diagnostics on. A fixture
            // that wants the leak recorded rather than reported calls ReleaseUploadsAsync itself,
            // before disposal, since by here the evidence file is already written.
            TestContext.Current.SendDiagnosticMessage(
                "The File Agent spike could not delete {0} uploaded file(s), which remain against the "
                + "account's quota: {1}",
                leaked.Count,
                string.Join(", ", leaked));
        }

        _chat.Dispose();
        _files.Dispose();
    }

    private void Trim()
    {
        if (_maxTurns <= 0)
        {
            return;
        }

        while (_turns.Count > _maxTurns)
        {
            _turns.RemoveAt(0);
        }
    }
}
#pragma warning restore OPENAI001, MEAI001
