using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Enterprise.Gpt.Integration.Test.FileAgentSpike;

/// <summary>
/// Attempts every conversion pair the matrix offers against a real sandbox, assigns each a confirmed
/// fidelity tier, and publishes the result as the single source the <c>document-conversion</c> skill
/// and <c>FileAgentOptions</c> both read.
/// </summary>
/// <remarks>
/// One test for the whole matrix: the container is billed by the minute and survives only while its
/// conversation stays in context, so the run is one pass down the source formats — seven scripts, not
/// forty-two. Tiers are graded by what happened, and a proposed refusal is still attempted, since a
/// refusal inherited from another environment's documentation is not evidence.
/// </remarks>
[Collection(SandboxSpikeCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConversionMatrixTests(SandboxSpikeFixture fixture)
{
    private readonly SandboxSpikeFixture _fixture = fixture;

    [Fact]
    public async Task ConversionMatrix_AttemptedAgainstTheSandbox_IsConfirmedCellByCell()
    {
        Assert.SkipUnless(_fixture.IsEnabled, SpikeConfiguration.SkipReason);

        var cancellationToken = TestContext.Current.CancellationToken;
        var session = _fixture.Session;
        var proposed = ConversionMatrix.Load();
        var inventory = await _fixture.InventoryAsync(cancellationToken);

        var container = await AuthorSamplesAsync(session, proposed.Formats, cancellationToken);

        Dictionary<(string From, string To), ConversionCell> confirmed = [];
        var recoveries = 0;

        foreach (var source in proposed.Formats)
        {
            // The samples live in the container, which is recycled once it falls out of context —
            // re-author the corpus on every change rather than assume it survived the pass.
            if (session.ContainerId != container)
            {
                container = await AuthorSamplesAsync(session, proposed.Formats, cancellationToken);
            }

            var targets = proposed.OfferedCells
                .Where(cell => cell.From == source)
                .Select(cell => cell.To)
                .ToList();

            if (targets.Count == 0)
            {
                continue;
            }

            var results = await AttemptAsync(session, source, targets, inventory, cancellationToken);

            if (!results.SourceFound)
            {
                // The container recycled between authoring the corpus and reading it. Retry once
                // rather than lose the pass and several minutes of billed sandbox time.
                container = await AuthorSamplesAsync(session, proposed.Formats, cancellationToken);
                results = await AttemptAsync(session, source, targets, inventory, cancellationToken);
                recoveries++;
            }

            foreach (var cell in proposed.OfferedCells.Where(cell => cell.From == source))
            {
                confirmed[(cell.From, cell.To)] = Grade(
                    cell,
                    results.Attempts.GetValueOrDefault(cell.To),
                    results.HostSideVerification.GetValueOrDefault(cell.To, "not checked"),
                    inventory);
            }

            // Per source format, not once at the end: a later throw would otherwise discard
            // everything learned so far, and these findings are the most expensive to reproduce.
            _fixture.Record(
                $"Which conversions from {source} does this sandbox serve?",
                string.Join(", ", targets.Select(target =>
                    $"{target}={confirmed[(source, target)].Tier}")),
                string.Join(" | ", targets.Select(target =>
                    $"{target}: {confirmed[(source, target)].Evidence?.Detail ?? "not attempted"}")));
        }

        var matrix = proposed with
        {
            Status = ConversionMatrix.Confirmed,
            RecordedUtc = DateTimeOffset.UtcNow,
            Deployment = session.DeploymentName,
            OfficeConverterPresent = inventory.OfficeConverter.Present,
            Cells = [.. proposed.Cells.Select(cell => confirmed.GetValueOrDefault((cell.From, cell.To), cell))],
        };

        var served = matrix.Cells.Count(cell => cell.Tier is ConversionMatrix.Faithful or ConversionMatrix.Structural);
        var refused = matrix.Cells.Count(cell => cell.Tier == ConversionMatrix.Refused);
        var moved = matrix.Cells.Where(cell => cell.Confirmed && cell.Tier != cell.ProposedTier).ToList();

        _fixture.Record(
            "Which conversion pairs does this sandbox actually serve, and at what fidelity?",
            $"{served} served, {refused} refused",
            $"Office converter present: {inventory.OfficeConverter.Present}. Cells that moved from their proposal: "
            + $"{(moved.Count == 0 ? "none" : string.Join("; ", moved.Select(cell => $"{cell.From} to {cell.To} was {cell.ProposedTier}, now {cell.Tier}")))}. "
            + $"The container was lost and the sample corpus re-authored {recoveries} time(s) during "
            + "the pass, which is the measure of whether a container survives a run of this length.");

        var unconfirmed = matrix.OfferedCells.Where(cell => !cell.Confirmed).ToList();

        Assert.True(
            unconfirmed.Count == 0,
            "Every offered pair has to be attempted before a tier can be published. Not attempted: "
            + string.Join(", ", unconfirmed.Select(cell => $"{cell.From} to {cell.To}")));

        if (SpikeConfiguration.IsRecording)
        {
            var path = await matrix.RecordAsync(cancellationToken);

            Assert.True(File.Exists(path));

            return;
        }

        var drift = matrix.Cells
            .Zip(proposed.Cells, (live, committed) => (live, committed))
            .Where(pair => pair.live.Tier != pair.committed.Tier)
            .Select(pair => $"  {pair.live.From} to {pair.live.To}: committed as {pair.committed.Tier}, now {pair.live.Tier}.")
            .ToList();

        Assert.True(
            drift.Count == 0,
            "The sandbox no longer serves what the committed matrix promises, so the published table and "
            + $"the skill that reads it are both wrong:{Environment.NewLine}{string.Join(Environment.NewLine, drift)}");
    }

    /// <summary>Authors the sample corpus in the sandbox and returns the container it now lives in.</summary>
    private async Task<string?> AuthorSamplesAsync(
        SandboxSession session, IReadOnlyList<string> formats, CancellationToken cancellationToken)
    {
        session.SetInputs();

        var run = await session.RunPythonAsync(SpikePaths.ReadScript("make-samples.py"), cancellationToken);

        // make-samples.py imports python-docx, openpyxl, python-pptx and reportlab at module scope,
        // so a library leaving the image kills it outright with no result line at all. Recorded here
        // because that failure costs a sandbox session to reproduce.
        JsonDocument result;

        try
        {
            result = await SpikeResults.RequireAsync(session, run, "samples.json", cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            _fixture.Record(
                "Can the sandbox author a sample of every format the matrix converts between?",
                "the corpus script did not complete",
                exception.Message);

            throw;
        }

        using var samples = result;

        // Authored in the container rather than uploaded: nothing on the API host can write a .docx
        // Word would recognise, and a sample nobody can regenerate rots. The script is deterministic,
        // and the inventory diff catches a library change that would alter what it produces.
        var authored = samples.RootElement.GetProperty("samples");
        var present = formats.Where(format => authored.TryGetProperty(format, out _)).ToList();
        var missing = formats.Except(present).ToList();

        // Before the assertion: an incomplete corpus is the most informative thing this step can
        // produce, and a run failing here would otherwise leave nothing behind.
        _fixture.Record(
            "Can the sandbox author a sample of every format the matrix converts between?",
            $"{present.Count} of {formats.Count}",
            string.Join(", ", present.Select(format =>
                $"{format} {authored.GetProperty(format).GetProperty("bytes").GetInt32()} bytes"))
            + (missing.Count == 0 ? "." : $". Absent: {string.Join(", ", missing)}."));

        Assert.True(
            missing.Count == 0,
            $"The sample corpus is incomplete, so pairs reading those formats cannot be attempted: {string.Join(", ", missing)}.");

        return session.ContainerId;
    }

    /// <summary>
    /// What one source format's probe produced: the sandbox's own account, and the API's independent
    /// second opinion on any artifact that made it back.
    /// </summary>
    /// <param name="SourceFound">False means the container recycled and took the corpus with it —
    /// recoverable, and not a statement about any conversion.</param>
    private sealed record AttemptResults(
        bool SourceFound,
        IReadOnlyDictionary<string, JsonElement> Attempts,
        IReadOnlyDictionary<string, string> HostSideVerification);

    private static async Task<AttemptResults> AttemptAsync(
        SandboxSession session,
        string source,
        IReadOnlyList<string> targets,
        SandboxInventory inventory,
        CancellationToken cancellationToken)
    {
        session.SetInputs();

        var script = SpikePaths.ReadScript("convert.py")
            .Replace("__SOURCE_FORMAT__", source, StringComparison.Ordinal)
            .Replace("__TARGETS__", JsonSerializer.Serialize(targets), StringComparison.Ordinal)
            .Replace("__OFFICE_BINARY__", inventory.OfficeConverter.Path ?? string.Empty, StringComparison.Ordinal);

        var run = await session.RunPythonAsync(script, cancellationToken);

        using var result = await SpikeResults.RequireAsync(
            session, run, $"conversion-{source}.json", cancellationToken);

        if (!result.RootElement.GetProperty("sourceFound").GetBoolean())
        {
            return new AttemptResults(SourceFound: false, ImmutableDictionary<string, JsonElement>.Empty,
                ImmutableDictionary<string, string>.Empty);
        }

        // Cloned out of the JsonDocument, which is disposed with this method.
        Dictionary<string, JsonElement> attempts = [];

        foreach (var attempt in result.RootElement.GetProperty("attempts").EnumerateArray())
        {
            Assert.True(
                attempt.GetProperty("to").GetString() is { Length: > 0 },
                $"The {source} probe returned an attempt with no target format, so nothing can be graded from it.");

            attempts[attempt.GetProperty("to").GetString()!] = attempt.Clone();
        }

        Dictionary<string, string> hostSide = [];

        // A second opinion the sandbox cannot give itself: the same bytes, opened on the API host
        // with none of the libraries that wrote them. It corroborates a tier and never sets one — an
        // artifact that never surfaces is a finding about the artifact channel, not the conversion.
        foreach (var target in targets)
        {
            var expected = $"converted-{source}-to-{target}.{target}";
            var artifact = run.Artifacts.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate.Name), expected, StringComparison.OrdinalIgnoreCase));

            if (artifact is null)
            {
                hostSide[target] = "no artifact reached the API";
                continue;
            }

            try
            {
                var bytes = await session.DownloadAsync(artifact, cancellationToken);
                var (verified, detail) = ArtifactVerifier.Verify(target, bytes);
                hostSide[target] = $"{(verified ? "verified on the host" : "failed on the host")}: {detail}";
            }
            catch (Exception exception)
            {
                hostSide[target] = $"could not be downloaded: {exception.GetType().Name}: {exception.Message}";
            }
        }

        return new AttemptResults(SourceFound: true, attempts, hostSide);
    }

    private static ConversionCell Grade(
        ConversionCell cell, JsonElement attempt, string hostSideVerification, SandboxInventory inventory)
    {
        if (attempt.ValueKind is JsonValueKind.Undefined)
        {
            return cell;
        }

        var produced = attempt.GetProperty("produced").GetBoolean();
        var engine = attempt.GetProperty("engine").GetString();
        var error = attempt.GetProperty("error").GetString();
        var degraded = attempt.TryGetProperty("degraded", out var flags)
            ? flags.EnumerateArray().Select(flag => flag.GetString()).Where(flag => flag is not null).ToList()
            : [];

        var verification = attempt.GetProperty("verification");
        var openable = verification.GetProperty("openable").GetBoolean();
        var detail = verification.GetProperty("detail").GetString() ?? string.Empty;

        var evidence = new ConversionEvidence(
            DateTimeOffset.UtcNow,
            engine,
            produced,
            attempt.GetProperty("bytes").GetInt64(),
            openable,
            (error is { Length: > 0 } ? $"{error} | " : string.Empty)
            + (degraded.Count > 0 ? $"degraded: {string.Join("; ", degraded)} | " : string.Empty)
            + $"sandbox: {detail} | host: {hostSideVerification}");

        var tier = (produced, openable, cell.ProposedTier) switch
        {
            // A pair that cannot produce a file something can open is refused, whatever the
            // proposal said; the recorded error is why.
            (false, _, _) or (_, false, _) => ConversionMatrix.Refused,

            // Bytes do not overturn a refusal: pdf-to-pptx yields one rasterised page per slide and
            // no editable content, which is the evidence for refusing it.
            (true, true, ConversionMatrix.Refused) => ConversionMatrix.Refused,

            // The one promotion: a headless office suite renders the source's own typography, which
            // is what the structural tier says is lost.
            (true, true, ConversionMatrix.Structural) when UsedOfficeConverter(engine, inventory) =>
                ConversionMatrix.Faithful,

            _ => cell.ProposedTier,
        };

        return cell with { Tier = tier, Confirmed = true, Engine = engine ?? cell.Engine, Evidence = evidence };
    }

    private static bool UsedOfficeConverter(string? engine, SandboxInventory inventory) =>
        inventory.OfficeConverter.Present
        && engine is { Length: > 0 }
        && Path.GetFileName(inventory.OfficeConverter.Path) is { Length: > 0 } binary
        && engine.Contains(binary, StringComparison.OrdinalIgnoreCase);
}
