# The File Agent

Enterprise GPT's assistant can now hand off "make me a spreadsheet of these numbers" to a specialised
agent that writes and runs Python in a hosted sandbox to actually produce the file, instead of pasting a
Markdown table into a chat bubble. This document covers how that agent is put together: its own model
and settings, the client it runs on, how a file gets in and a produced artifact gets back out through an
SDK bridge that was not built to carry either, its skills, and the guarantees that keep every line of
Python it runs inside the sandbox.

**Scope.** This covers Waves 1 through 3 — the agent's composition, its model, its skills, the
mount/harvest mechanism, what a run actually does (the refusals it makes before opening a sandbox, the
check every artifact passes before it is stored, how a run's outcome is classified and reported, and what
it costs), the permission that gates the whole capability (§12), and the rollback lever that switches it
off without a deployment (§13). For where a produced file is stored and delivered, see
[`generated-files.md`](generated-files.md); for what the sandbox itself can and cannot do, see
[`sandbox-capabilities.md`](sandbox-capabilities.md).

## 1. `FileAgentOptions`, and every setting

Bound from the `FileAgent` configuration section
(`Enterprise.Gpt.Service/Settings/FileAgentOptions.cs`), validated with
`AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(...).ValidateOnStart()` — the same shape
`SummarizationOptions` uses.

| Setting | Default | Range | What it governs |
| --- | --- | --- | --- |
| `Enabled` | `false` | — | Whether the tool is offered to the model at all. This is the feature's whole rollback lever: off, the tool is never attached and no sandbox session ever starts. A file already generated stays downloadable regardless — this governs new generation, not access to what exists. The **committed** `appsettings.json` also ships `false` — unlike `Summarization:Enabled`/`SheetQuery:Enabled`, whose committed files turn them on for development — because this is the one of the three that bills per run; see §13.6. |
| `ModelId` | *(required)* | non-empty `Guid` | The `Core.Ref.Model` row the agent runs on. See §2. |
| `ToolTimeoutSeconds` | `300` | 30–1800 | The wall-clock ceiling on one `file_agent` call, independent of the outer turn's own bounds — see §4. |
| `MaxArtifactsPerRun` | `3` | 1–10 | How many artifacts one run may persist; the surplus is dropped and reported rather than failing a run that also produced what was asked for. |
| `MaxIterationsPerRun` | `12` | 2–40 | The agent's own `MaximumIterationsPerRequest`, set on its dedicated client (§4) — its own number because the agent spends iterations the outer turn does not (load a skill, read its reference, run the code, re-open the artifact). |
| `MaxVerificationRetries` | `1` | 0–3 | How many times a run may regenerate an artifact that failed its check (§5.3). Zero is legitimate: one attempt, and a deployment that would rather not pay for a second. |
| `MaxRunsPerUserPerDay` | *(none)* | 1–10 000 | Runs one user may start in a rolling day, or unset for no ceiling (§11.1). |
| `MaxSandboxSecondsPerUserPerDay` | *(none)* | 1–1 000 000 | Seconds of file generation one user may spend in a rolling day, or unset for no ceiling (§11.1). |

`Enabled` is validated by neither `[Range]` nor `Validate(...)` (it is a plain `bool`), but `ModelId` is
explicitly checked non-empty — `options.ModelId != Guid.Empty` — because a `Guid` has no natural "unset"
`[Range]` to express.

## 2. The pinned catalog model, and the startup validator

The agent runs on its own `Core.Ref.Model` row rather than following the conversation's own selected
model — seeded by migration `20260828052027_SeedFileAgentModel`, pointed at `Providers.AzureOpenAI`,
with `IsUserSelectable = false` so it never appears in the chat picker, matching the summarizer row's own
precedent. It is a row of its own, not a repurposed summarizer row, so the two can be repointed by an
administrator independently.

`IFileAgentModelResolver` (`Enterprise.Gpt.Service/Agents/FileAgentModelResolver.cs`) reads it **per
call**, never cached, so an administrator editing the row's deployment takes effect on the next turn —
and a deactivated row is treated as absent, standing the agent down rather than silently billing against
a deployment an operator believes they withdrew. It checks one more thing no `[Range]` attribute can:
that the row's `ProviderId` actually resolves to Azure OpenAI. This is checked explicitly, ahead of
reaching the provider, because the failure mode of *not* checking it is silent — the Chat Completions
bridge the Azure AI Foundry provider uses drops a hosted tool instead of rejecting it, so a misrouted
agent would run and simply never produce a file.

`FileAgentBootstrapper.ValidateAsync` (`Enterprise.Gpt.Api/Startup/FileAgentBootstrapper.cs`) runs at
startup **regardless of `FileAgent:Enabled`**, for the same reason `SummarizerBootstrapper` does: a
misconfigured deployment should fail at deploy time, not on the first request after someone flips the
flag on months later. It resolves the pinned model, confirms both a chat client for that model's provider
and the agent's own keyed client (§4) exist, forces the instruction template and the skill directory to
be read off disk — so a template left out of the build, or a skills directory that failed to copy, fails
the deploy rather than the first user request.

## 3. The dedicated `ChatClientKeys.FileAgent` client

The agent does not share the main `ChatClientKeys.AzureOpenAI` client. It gets its own keyed
registration, over the identical `OpenAIClient`/Responses route, with two deliberate differences from
the shared one:

**Its own function-invocation bounds.** `MaximumIterationsPerRequest` (bound to
`FileAgent:MaxIterationsPerRun`), `AllowConcurrentInvocation = false`, `IncludeDetailedErrors = true`,
and `MaximumConsecutiveErrorsPerRequest = 5` are configured on this client alone. They have to be:
`MaximumIterationsPerRequest` is an **instance** setting with no per-request override, so sharing the
turn's own client would silently cap the agent at the outer turn's own ceiling of 5 — nowhere near enough
for load-skill → read-reference → run-code → re-open-and-check.

**No `.UseToolTracking(...)`.** This is deliberate and is the direct cause of `trackUsage: true` in §5. A
nested tool tracker would open its own root scope, with no writer attached to it — swallowing every
`ChatProgress` line the agent's run reports, and putting its own child activities out of reach of the
turn's stream entirely. Leaving tracking off keeps the ambient activity scope pointing at the enclosing
`file_agent` tool call, which is what lets the agent's steps nest underneath it in the timeline at all.

`ChatClientKeys.FileAgent` is **not a provider key** — no `Core.Ref.Provider` row maps to it, and
`Providers.ServiceKeys` never resolves it. It exists purely because the two differences above are client
*instance* settings, not per-request options.

`Program.cs` also extracts the shared `OpenAIClient` construction into its own singleton, reused by the
`AzureOpenAI` client, the `FileAgent` client, the embedding generator, and the hosted-file client below —
previously each factory constructed its own. It is registered as a factory rather than built eagerly, for
the same reason the embedding generator's own comment already states: constructing it here would run
`new Uri(...)` ahead of `ValidateOnStart`, turning a blank `AzureOpenAI:Url` into a bare
`UriFormatException` instead of the validator's own message.

The same registration block adds `IHostedFileClient` (`OpenAIClient.AsIHostedFileClient()`), used by
`FileAgentSandbox` for every upload and download and by the bootstrapper's own startup check. It has to
be built from `OpenAIClient` directly — the narrower `GetFileClient()` overload cannot see a file the
sandbox wrote into its own container, and answers 404 for every artifact.

## 4. Composing the agent for a turn

`IFileAgentToolProvider`/`IFileAgentToolLease` (`Enterprise.Gpt.Service/Agents/FileAgentToolProvider.cs`)
mirror `IMcpToolProvider`/`IMcpToolLeaseSet` deliberately: a turn borrows a tool, uses it, and gives it
back. The interfaces live in `Enterprise.Gpt.Service`; the implementation lives in
`Enterprise.Gpt.Api.Agents.FileAgentToolProvider`, because `Microsoft.Agents.AI` is a package reference
`Enterprise.Gpt.Service.csproj` deliberately excludes — a comment there records the exclusion, and this
is the abstraction that lets `Enterprise.Gpt.Service` reach the agent without ever referencing it.

`AcquireAsync(conversationId, userId, cancellationToken)`:

1. Resolves the pinned model (§2) and the `ChatClientKeys.FileAgent` client (§3).
2. Lists the conversation's own file names (`IFileAgentDocumentReader.ListNamesAsync`), which seed the
   agent's instructions so it knows what it may be asked to work from.
3. Builds a `FileAgentSandbox` (upload/download/harvest, §7–8) and an `AgentSkillsProvider` (§6), both
   scoped to the lease and released on disposal.
4. Constructs a `ChatClientAgent` named `"File Agent"`, with a `HostedCodeInterpreterTool` whose `Inputs`
   list is the *same mutable list* the middleware in step 5 fills per call, and
   `UseProvidedChatClientAsIs = true` — left `false`, the agent's own middleware would attach this turn's
   tools onto the shared client's function invoker, where the *next* turn would inherit them.
5. Wraps the agent with agent-level middleware (`.AsBuilder().Use(run.RunAsync, ...)`) — this is the
   mechanism described in §5.
6. Wraps the result with `AIAgent.WithTracking(...)`, naming the tool `file_agent` and passing
   `trackUsage: true`.

`ConversationService` attaches the tool through the same gate ladder `document_summarize` already climbs,
plus a `Generate Files` permission rung of its own (§12) — and treats a stood-down agent as a lost
capability, not a failed turn: `FileAgent:Enabled` off, a missing grant, a model with no tool support, or
a name collision with a user's own MCP selection each simply skip attaching it. Every rung but the grant
check logs a warning; the grant check is the one silent rung, for the reason §12 explains. A composition
failure (the model row misconfigured, the client missing) is caught and logged at error rather than
failing the turn, on the theory that the startup validator (§2) is where that should already have
surfaced.

## 5. How an artifact gets out of a string-in/string-out bridge

`AIAgent.AsAIFunction()` — and `WithTracking(...)`, which wraps it — exposes the agent as exactly one
string parameter in, one string out. `Microsoft.Agents.AI`'s own XML docs confirm this plainly. That
leaves a real problem: the run's own `AgentResponse`, where a produced artifact's identity actually
appears (as `CodeInterpreterToolResultContent`, a bare `HostedFileContent`, or a `CitationAnnotation` —
see [`sandbox-capabilities.md`](sandbox-capabilities.md) §2), is never visible to whatever calls the
string-bridged function.

The chosen mechanism is **agent-level middleware** — `AIAgentBuilder.Use(runFunc, runStreamingFunc)` —
wrapping the whole run rather than sitting outside the tracked call. `FileAgentRun.RunAsync` runs before
the inner agent call to mount inputs (§7) and after it to harvest and store outputs (§8), and because it
wraps the *same* call `WithTracking` wraps, it sees the genuine `AgentResponse` the string bridge would
otherwise hide entirely.

Two alternatives were weighed and rejected:

- **An agent-scoped "save this file" tool the model itself calls.** Rejected because it depends on the
  model remembering to call it — an inference-time behaviour with nothing structural enforcing it. A
  model that forgot would produce a file that never gets stored.
- **`ChatClientAgentRunOptions.ChatClientFactory`.** Rejected because it is never applied on this path:
  `AsAIFunction()` constructs a base `AgentRunOptions`, and only the derived `ChatClientAgentRunOptions`
  type carries a factory — so a hook that looks reachable from the SDK's public surface simply never
  fires here.

What the assistant reads back from the tool call — `Describe(result)` — is not the agent's raw text
verbatim. A successful run gets the file's name and its **measured** shape appended
(`"[Saved and attached to your answer: summary.xlsx (2 sheets (Revenue, Detail)).]"`), so an answer that
quotes it is stating what the file actually holds rather than what was asked for. A run that stored
nothing but did not fail gets `"[No file was saved. … Do not describe a file as though the user has
one.]"` — see §5.1 for which of those two a run is.

### 5.1 The nine outcomes a run can have

Every `file_agent` invocation ends in exactly one outcome
(`Enterprise.Gpt.Service/Agents/FileAgentOutcomes.cs`), decided by the middleware rather than reported by
the model. Three of them are **refusals**, which return an answer and complete the activity; the rest are
**failures**, which throw a sanitized `InvalidOperationException` so the tracking wrapper renders the
activity failed and `IncludeDetailedErrors` hands the assistant a sentence it can explain.

| Outcome | Reached when | Surfaces as |
| --- | --- | --- |
| `refused-conversion` | The pre-flight (§5.2) matched one source and one target, and the matrix marks the pair refused | An answer naming the pair and a supported alternative. **No sandbox session.** |
| `refused-ambiguous` | A name in the instruction belongs to more than one live document | An answer asking which. No sandbox session. |
| `refused-unknown-source` | The instruction names a file the conversation does not have | An answer naming what it does have. No sandbox session. |
| `no-file-produced` | The run returned and stored nothing, and nothing failed a check | A failed activity reading "No file was produced" |
| `verification-failed` | Every artifact failed to re-open, and the retry bound is spent | A failed activity reading "The file did not open when checked" |
| `timed-out` | `ToolTimeoutSeconds` elapsed | A failed activity reading "Stopped at the time limit" |
| `cancelled` | The turn was stopped | The cancellation propagates; the turn's own unwind handles it |
| `error` | Anything else threw | A failed activity |
| `created` | At least one artifact verified and was stored | The answer, plus the chip |

The four failures' wording lives in one place, `FileAgentFailures.cs` — a `FileAgentFailure(Outcome,
Message, SubStatus)` record per failure — rather than at each throw site, because progress events
deliberately never carry error text: the line reported just before the scope fails is the only channel a
reason has, and a single "something went wrong" would collapse the distinction the timeline exists to
draw. `FileAgentFailuresTests` asserts all seven lines a card can show — the four failures above plus the
three refusals — read as seven distinct strings.

Telling a refusal apart from a failure is what the pre-flight makes deterministic: a refusal never
reaches the sandbox, so a run that answers with prose *after* calling the code interpreter and produces
nothing is a failure, whatever the prose said.

### 5.2 The pre-flight, and why it is deliberately narrow

`FileAgentPreflight.Evaluate` (`Enterprise.Gpt.Service/Agents/`) runs before the inner agent call. It
refuses a conversion **only** when exactly one source is resolved, exactly one target format is named,
and the confirmed matrix marks that cell `refused` — today `pptx` → `pdf` and `pdf` → `pptx`, and nothing
else. Anything less certain runs.

That narrowness is the point. Refusing a pair the sandbox can genuinely perform is as much a defect as
promising one it cannot, so a `◐` structural cell is always served with its caveat and never declined; a
test enumerates every published cell and asserts exactly that.

Target detection (`FileAgentFormats.DetectTargets`) removes the resolved source's own file name before
scanning, because "convert deck.pptx to pdf" otherwise reads its own source as a second target and no
request naming a source would ever name exactly one. Matching is on whole words and the vocabulary is
deliberately narrower than the skill filter's: "document" and "sheet" are excluded, because a wrong
reading here refuses a conversion where a wrong reading there merely advertises an unwanted skill.

### 5.3 Verification: on this host, not in the sandbox

Every artifact is downloaded, **re-opened and measured before it is stored**
(`Enterprise.Gpt.Service/Agents/GeneratedArtifactVerifier.cs`): `DocumentFormat.OpenXml` for
`docx`/`xlsx`/`pptx`, `PdfSharp` for `pdf`, `CsvHelper` for `csv`, a throwing UTF-8 decode for `md`/`txt`.
It reports what it found — paragraphs and tables, sheet names, slide count, page count, columns and rows —
and that measurement is what the answer quotes.

It runs **on the API host rather than as a second sandbox pass**, which is a deliberate departure from
the PRD's wording. The PRD asks for both "a second sandbox pass" and "makes no model call", and on the
Responses route those cannot both hold: code runs in the sandbox only when a model emits it. Host-side is
literally deterministic, costs no tokens and no sandbox seconds, and checks the exact bytes that will be
stored and downloaded rather than a copy still inside the container. Nothing here runs Python on the host —
opening a package with the Open XML SDK is not script execution, and §9's guarantees are untouched.

A failed check does not store the file. While no artifact has verified, the middleware appends the
failure to the conversation and calls the agent again — bounded by `MaxVerificationRetries` (default `1`)
and by the run's own deadline. Once that is spent with nothing verified, the run throws
`verification-failed`, and **no row and no blob exist**, because nothing was ever stored.

### 5.4 What the timeline shows

The agent's client carries no tool tracking, so a bare `ChatProgress.Report(...)` lands as a sub-status
on the agent's own card. Three steps open a **nested scope** instead
(`ChatProgress.BeginToolScope`), which is what draws them as `depth: 2` cards beneath the agent:
`Preparing files` (only when a source file is mounted), `Running code` (once per attempt), and `Checking
the file` (once per artifact `StoreAsync` processes).

The descriptor carries prose in **`Name` as well as `DisplayName`**. The card reads its label off `Name`;
only the status line the tracker composes uses `DisplayName`. `Name` is a **constant** — `"Checking the
file"`, never the artifact's own name — and a code identifier there would put `run_code` on screen. The
file name goes on the ephemeral `ChatProgress.Report($"Checking {name}")` line instead, which is never
persisted.

**Each of those three steps is its own row in `Core.ConversationUsageToolCall`** — already, per
[`usage-and-favorites.md` §6.3](../conversations/usage-and-favorites.md#63-coreconversationusagetoolcall),
the largest table in the schema — nested beneath the `file_agent` call's own row rather than folded into
it. A creation run with no source file and no verification retry adds two nested rows; an edit or a
convert, which mounts one, adds three; a verification retry (§5.3) repeats `Running code` and `Checking
the file` for each further attempt. Worth knowing before writing a report against this table — a nested
scope is exactly what makes the timeline in §5.1 renderable and auditable, not a leak.

### 5.5 Cancellation

A turn that is stopped or fails after the agent has already stored a file withdraws it:
`IFileAgentToolLease.DiscardGeneratedAsync()` soft-deletes each row and deletes its blob, called from
`ConversationService`'s streaming `finally` when the turn did not complete. A generated file reaches the
transcript only on a completed turn, so a row left behind by an abandoned one would show the user a file
no message introduced. The row-first ordering, the DI scope and why the write is uncancellable are
[`generated-files.md` §4](generated-files.md#4-withdrawing-a-file-the-turn-did-not-deliver)'s to describe.

## 6. Skills, and their two-stage trimming

Nine skills ship under `Enterprise.Gpt.Service/Agents/Documents/Skills/`, one `SKILL.md` per directory:
`docx-authoring`, `xlsx-authoring`, `pptx-authoring`, `pdf-authoring`, `csv-tabular`, `markdown-text`,
`document-comparison`, `document-conversion`, `artifact-verification`. They ship the same way the
existing prompt templates do — a globbed `<None Update="Agents\Documents\Skills\**\*.md">` csproj rule,
because a skill is a directory of files rather than one named template — and `FileAgentSkills.Discover`
fails loudly (`DirectoryNotFoundException`) if the deployed set does not match what the code expects,
both at startup (§2) and in a unit test that pins the exact nine names.

Two stages of trimming exist, and the PRD is explicit that either alone is only half the mechanism:

- **Advertise-stage** (`AgentSkillsProviderBuilder.UseFilter(...)`) — keeps a run from spending tokens on
  a skill's ~100-token advertisement when it cannot use it.
- **Load-stage** (progressive disclosure: `load_skill` → `read_skill_resource` → `run_skill_script`) —
  keeps a skill's own body out of context until the model actually asks for it, capped under the
  recommended 5,000-token ceiling per load.

`FileAgentSkills.Select(instruction)` matches a static topic table against whichever string it is given —
"docx"/"word"/"document" implies `docx-authoring`, "compare"/"diff"/"changed" implies
`document-comparison`, and so on. `artifact-verification` rides along with any match, since every run
should re-check its own output. An instruction matching no topic advertises **everything** — the safe
direction to fail, since an unadvertised skill is one the model has no way to ask for.

### 6.1 How the filter sees an instruction that does not exist yet

`AcquireAsync` builds the skills provider as part of tool assembly, **before the model has produced any
output for the turn** — the model has not decided to call `file_agent`, let alone said what it wants
built. So the provider is not handed a string; it is handed a function:

```csharp
var instruction = new RunInstruction();

FileAgentSkills.CreateProvider(FileAgentSkills.DeployedRoot, () => instruction.Text, _loggerFactory)
```

`FileAgentRun.RunAsync` sets `instruction.Text` from the run's own user messages before it calls the
inner agent, and the filter — which runs when the provider is consulted, not when it is built — reads
it then. Skill caching is switched off (`DisableCaching()`) for the same reason: a cached
advertisement would be the first call's answer whatever the second call asked for, and one turn can
call the agent twice.

Matching is on **whole words**, not substrings: `"md"` occurs inside "command" and `"text"` inside
"context", and a topic that matches everything advertises everything, which is the cost this filter
exists to avoid.

The provider is built **fresh per turn**, never cached across turns — the filter closes over that
turn's own state, and a shared provider would leak one conversation's formats into another's
advertisement.

## 7. Mounting inputs: how a source file reaches the sandbox

Because the tool's whole outward schema is one natural-language string (§5), there is no structured
`sourceDocumentNames` parameter for the model to fill in — resolution happens inside the agent's own
pipeline, ahead of the model seeing the request.

`FileAgentRun.MountAsync` matches document names occurring **verbatim, case-insensitively** in the
model's instruction text against the files available in the conversation
(`IFileAgentDocumentReader.ReadMentionedAsync`), longest name first — so `"report.docx"` cannot
shadow-match inside `"final report.docx"` — downloads each match's blob, uploads it to the Files API via
`FileAgentSandbox.UploadAsync`, and adds the result to the **same shared** `HostedCodeInterpreterTool.Inputs`
list the tool instance was constructed with.

`IFileAgentDocumentReader` (`Enterprise.Gpt.Service/Agents/FileAgentDocumentReader.cs`) deliberately
covers a *wider* corpus than `DocumentRetrievalService`'s own scope: it must see a conversation's
generated documents too, or the agent could never edit or convert a file it produced earlier — where
retrieval must never see one at all (see [`generated-files.md`](generated-files.md) §6). Both apply the
identical ownership rule the download route already enforces.

Mutating one shared `Inputs` list per turn, rather than allocating a fresh tool per call, is safe **only**
because `AllowConcurrentInvocation = false` on both the outer turn's client and the agent's own client
(§3) — at most one call executes at a time, so the mutation between calls never races a read.

## 8. Harvesting artifacts: how a produced file comes back

`FileAgentSandbox.Harvest` (`Enterprise.Gpt.Api/Agents/FileAgentSandbox.cs`) walks **every channel** a
produced file can arrive on — `CodeInterpreterToolResultContent.Outputs`, a bare `HostedFileContent` on
the message, and `CitationAnnotation` (the channel this deployment was actually confirmed to answer on;
see [`sandbox-capabilities.md`](sandbox-capabilities.md) §2) — de-duplicated by file id, with the code
interpreter's own container id stamped onto every artifact once found anywhere in the walk. The container
id is read via reflection off a non-public SDK type, because `CitationAnnotation` carries no public
member for it.

Downloading requires that container id: without it, the call resolves against the standard Files API,
which cannot see a file the sandbox wrote, and answers 404.

`FileAgentRun.StoreAsync` then, for each artifact up to `MaxArtifactsPerRun`: downloads it
container-scoped, and persists it through `IGeneratedDocumentService.StoreAsync` — the write path
[`generated-files.md`](generated-files.md) §3 describes. A surplus past the ceiling is dropped and
reported as a refusal rather than a failure; a `ValidationException` from the store (over the size limit,
an unsupported extension) is folded into the same refusal list rather than thrown, so a run that produced
one good file and one oversized one still delivers the good one.

On lease disposal, `FileAgentSandbox.ReleaseAsync` deletes every file the turn uploaded as an *input* —
best effort, logged, never thrown, since a leak costs file quota against a turn that has already
answered and nothing else would notice a thrown exception here.

## 9. The three guarantees that keep script execution off the API host

Every line of Python this feature runs, runs inside the hosted sandbox — never as a subprocess on the API
host. Three things enforce that together:

1. **`AgentFileSkillsSourceOptions.AllowedScriptExtensions = []`.** No file is ever discovered as a
   runnable script, whatever a skill's own directory happens to contain.
2. **`run_skill_script` keeps its approval requirement.** Unlike the two read-only tools (§10),
   `DisableRunSkillScriptApproval` is left `false` — nothing in a headless server turn can answer an
   approval request, so this call path stalls rather than executing.
3. **Exactly one `AgentFileSkillScriptRunner` is registered anywhere in the solution, and it throws**
   (`FileAgentSkills.RefuseScriptAsync`). "Zero call sites" is not an achievable guard: the package's
   `AgentSkillsProviderBuilder.Build()` refuses to construct a file-skill provider with **no** runner
   registered at all. A refusing runner is therefore the strongest guard actually available — and it is
   stricter than absence would be, because it fails loudly rather than by omission. A code-search test
   (`Solution_RegistersExactlyOneSkillScriptRunner_AndItRefuses`) pins that this is the only call site,
   so a second, real runner added later fails the build.

## 10. Read-only skill approval

`load_skill` and `read_skill_resource` are switched off from requiring approval via
`AgentSkillsProviderOptions { DisableLoadSkillApproval = true, DisableReadSkillResourceApproval = true }`,
set through the builder's `.UseOptions(...)`. Left unconfigured, every call to either tool returns
`ToolApprovalRequestContent` instead of executing — a stall nobody can answer in a server turn with no
human present.

**Why not `UseToolApproval(...)` with `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule`** — the
mechanism the PRD originally proposed? That rule depends on approval decorators the SDK wires around a
client it manages itself. `UseProvidedChatClientAsIs = true` (§4) means the agent's client is *not*
managed that way, so those decorators are simply absent — the rule would have to be wired by hand for a
guarantee no stronger than disabling the two approval gates directly on the provider, which is what
shipped instead.

## 11. Configuration reference

| Setting | Section | See |
| --- | --- | --- |
| `FileAgent:*` | `FileAgentOptions` | §1 |
| `AzureStorage:GeneratedContainer` | Write path | [`generated-files.md`](generated-files.md) §2 |

### 11.1 The per-user ceiling

Both ceilings are **opt-in**: unset, the feature behaves exactly as it did without them, and
`FileAgentQuotaService` never queries. Set, they are counted from the audit rows themselves — `Kind =
Agent`, `Depth = 0`, `ModelId` equal to the agent's pinned row, over a rolling 24 hours — so what the
ceiling counts and what the bill counts are the same rows. A **rolling** window rather than a calendar
day, because a UTC midnight reset lands in the middle of the working afternoon somewhere.

At the ceiling the tool is **not attached**, and one sentence is added to the turn's instructions telling
the assistant the limit was reached so it can say so. That is the only stand-down the assistant is told
about: every other reason the agent is absent is a capability the user never had, and announcing one
would advertise something they cannot use.

The sandbox-seconds ceiling reads the agent tool call's own recorded duration, which is the durable proxy
for billed session time — the precise figure lives in the `sandbox.duration` metric (§11.2), which
nothing can query back.

### 11.2 Telemetry

Four instruments on the existing chat `Meter` (`Enterprise.Gpt.Service/Observability/ChatMetrics.cs`), so
the exporter registration picks them up with no wiring change:

| Instrument | Type | Dimensions |
| --- | --- | --- |
| `enterprise_gpt.file_agent.run.duration` | Histogram, `s` | `file_agent.outcome` |
| `enterprise_gpt.file_agent.verification` | Counter, `{artifact}` | `file_agent.outcome` (`passed`/`failed`), `document.type` |
| `enterprise_gpt.file_agent.sandbox.duration` | Histogram, `s` | `file_agent.outcome` |
| `enterprise_gpt.file_agent.sandbox.active` | UpDownCounter, `{session}` | — |

Plus one span per run, `file_agent.run`, from an `ActivitySource` named with the same
`TelemetryNames.ChatSource` (`Observability/FileAgentTracing.cs`), tagged with the outcome and with an
error status for every outcome that leaves the user without a file — a refusal is a correct answer and is
tagged `Ok`.

No dimension carries prompt content, a tool argument, generated source, a signed URL or a user-supplied
file name. `document.type` is the extension, not the name, and the question it answers — which formats
fail — is the one the artifact-validity criterion asks.

`sandbox.duration` measures the model round trip that carries the code interpreter, which is the only
observable proxy for billed session seconds on this route: the provider bills the session separately from
tokens and reports neither back.

### 11.3 Usage attribution

`UsageReportTranslator` fills `ModelId`/`DeploymentName` on a tool-call row when — and only when — the row
is `Kind = Agent` **and** its tool name is `file_agent`. Its own nested calls are the agent's tools rather
than further model turns, so they still write null, as does every other tool kind; that is the honest
answer rather than a gap. A `file_agent` row on a turn with no resolved agent model — the misconfiguration
the startup validator (§2) should have caught — leaves the columns null and logs a warning, because the
column is a foreign key and this write runs after the answer has already streamed.

`trackUsage: true` (§5) is what puts the agent's own tokens on that row without touching the turn's. The
`(ModelId, DateCreated)` index `ConversationUsageToolCall` deferred "until the first agent that reports a
model" now exists — migration `20260828195328_AddConversationUsageToolCallModelIndex`, filtered `WHERE
[ModelId] IS NOT NULL` and replacing the unfiltered, EF-by-convention single-column `ModelId` index — and
is what the ceiling above reads.

## 12. The Generate Files permission gate

Every capability described above still needs one more thing before it reaches a caller: the
**`Generate Files`** permission (`PermissionIds.GenerateFiles`), seeded as a third built-in
`Core.Permission` row alongside `Administrator` and `Upload File` — by `PermissionConfiguration.HasData`
for a database built from empty, and by migration `20260828214951_SeedGenerateFilesPermission` for one
that already exists. Unlike `Upload File`, its `IsDefault` is `false`: every run this permits provisions
a billed sandbox session, so it is granted deliberately rather than handed to every user, existing or new,
at sign-in.

**Where it sits in the ladder.** The grant check runs in `ConversationService.CreateChatOptionsAsync`,
after the `FileAgent:Enabled` check and the model-supports-tools check, and **before** the tool-name
collision check, `IFileAgentToolProvider.AcquireAsync`, and the per-user quota (§11.1). It reads
`IUserGrantReader.GetGrantsAsync` — the same singleton `IUserPermissionCache` every other gated check in
this API reads, not a per-request query; see [Permission Cache](../permissions/permission-cache.md) for
how that cache is warmed, invalidated, and bounded. Missing the grant stands the agent down with **no log
line and no instruction appended to the turn** — the one silent rung in this whole ladder, because every
rung above and below it either logs a warning or tells the assistant something about a capability it
could otherwise have used. A caller who was never offered the capability has nothing to be told; a stood-
down agent still reads as a lost capability, not a failed turn, exactly as the rest of the ladder already
treats one.

**Administrators are not implicit holders.** `PermissionIds.Administrator` gates admin routes and nothing
else (see [Permission Cache §2](../permissions/permission-cache.md#2-quick-start--gating-an-endpoint)),
so an administrator who lacks `Generate Files` gets the identical silent stand-down as anyone else — a
test pins this explicitly (`StreamConversationAsync_AdministratorWithoutTheGenerateFilesGrant_HasNoFileAgent`).

**The grant is read once per turn, not once per conversation.** A revocation therefore lands on the
caller's very next turn — it is read before the tool is acquired, so it never interrupts a turn already
streaming with the tool attached.

**Granting and revoking go through the existing surface; mutating the row itself does not.** An
administrator grants or revokes `Generate Files` through the same `api/permissions` /
`api/users/{id}/permissions` routes every other permission uses — there is no new endpoint. What they
cannot do is rename or deactivate the row: `PermissionService.EnsurePermissionIsCustom` now walks
`PermissionIds.Names` rather than checking `Administrator` and `Upload File` by two separate `if`s, so
`Generate Files` gets the same protection for free, and the next built-in permission will too, as long as
it is added to `Names` — already a requirement (see
[Permission Cache §3](../permissions/permission-cache.md#3-permission-names-are-resolved-from-a-static-map-not-the-database)).

**The migration's `Down` is narrower than it looks.** It deletes the seeded row outright, which succeeds
only while nobody holds the grant — the `UserPermission` foreign key is `NoAction` and a revoked grant is
soft-deleted rather than removed, so rolling this migration back on a deployment that ever used the
feature means clearing those grant rows by hand first.

## 13. Rollback

`FileAgent:Enabled` is the feature's entire rollback lever — with it off, the tool is never attached to
any turn and the model cannot discover or call it (§1). This section exists for the reason
[`sheet-query.md` §10](../documents/sheet-query.md#10-rollback) gives its own rollback lever a section of
its own: worth documenting on its own terms, not left as one row in a settings table.

### 13.1 What changed

Nothing about the mechanism changed this wave — `FileAgentOptions.Enabled` already defaulted to `false`
in code, and the stand-down ladder (§4, §12) already read it, since Wave 1. What changed is the
**committed** [`appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json): it
shipped `true`, directly contradicting the option's own remark ("Defaults off everywhere, development
included"), and now ships `false`.
[`FileAgentOptionsTests.Bind_TheShippedConfiguration_LeavesFileGenerationOff`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/FileAgentOptionsTests.cs)
binds the real file and fails the build if that ever drifts back.

### 13.2 Why not a live-reload provider, the way `SheetQueryOptions` got one

`SheetQueryOptions` is read through a purpose-built `ISheetQueryOptionsProvider` that rebuilds itself on
every configuration reload, deliberately avoiding the callback-thread crash a bare `IOptionsMonitor<T>`
risks (see [`sheet-query.md` §10.2](../documents/sheet-query.md#102-why-not-ioptionsmonitorsheetqueryoptions)).
`FileAgentOptions` took none of that: `ConversationService` resolves plain `IOptions<FileAgentOptions>`
and caches `.Value` in a constructor field, exactly as `SummarizationOptions` does. `IOptions<T>`'s value
is computed once, the first time anything resolves it, and held for the life of the process — a later
edit to `appsettings.json`, reloading or not, never reaches it.

The story this closes asks to switch file generation off "without a deployment," not without a restart —
the stronger guarantee `SheetQuery:Enabled` earns for a flag that ships **on** in every developer's
environment by default. `FileAgent:Enabled` ships **off** everywhere, so the pressure to flip it live,
mid-incident, with no restart at all, is lower: a runaway sandbox session is already bounded by its own
`ToolTimeoutSeconds` and by the per-user quota (§11.1) regardless of the flag. Building
`ISheetQueryOptionsProvider`'s shape a second time here would add real complexity for a lever this
story's own acceptance criteria does not ask for.

### 13.3 What reaches the next turn — and what needs a restart

An edit to `appsettings.json`, an environment variable, or an Azure App Service Application Setting all
need the same thing here: **a restart**. `IOptions<FileAgentOptions>` binds once, at first resolution,
from whatever `IConfiguration` said at that moment — unlike `SheetQuery:Enabled`, there is no
reloading-file-provider shortcut that skips it. In practice this costs little: an Azure App Service
Application Setting change already restarts the app on its own, and a container environment variable
already needs a new container regardless. The one case where this is a real cost is a bare
`appsettings.json` edit on a long-running process with no orchestrator watching it — that edit needs an
explicit restart, where the identical edit under `SheetQuery:Enabled` would not.

### 13.4 What flipping the switch does not touch

`FileAgentOptions` is read in exactly the places §1's settings table, §2's model resolver, and §12's
permission gate describe. Turning the flag off touches none of `FileAgentBootstrapper`'s startup
validation, which runs **regardless of the flag** (§2) — a misconfigured pinned model fails the deploy
whether or not anyone can reach it yet — none of the skills on disk (§6), and nothing already stored: a
file a run produced while the flag was on stays downloadable after it is switched off, because the
download route is gated on conversation ownership alone, never on this flag or on the `Generate Files`
permission — see [`generated-files.md` §5](generated-files.md#5-downloading-a-generated-document).

### 13.5 Rehearsing it

1. With the flag off, take a turn asking for a generated file in a conversation where the caller holds
   `Generate Files`. Confirm the assistant never mentions the capability and no `file_agent` call appears
   in the activity feed.
2. Flip `FileAgent:Enabled` to `true` and **restart the process**. A request against a still-running
   instance from before the restart must still show no tool — proving the flag genuinely needs one.
3. Take a turn in the same conversation. Confirm `file_agent` now attaches and the request produces a
   file.
4. Flip it back to `false`, restart again, and confirm the file produced in step 3 still downloads — the
   flag governs new generation, never access to what already exists.

### 13.6 The default is the committed file too — unlike its siblings, deliberately

`Summarization:Enabled` and `SheetQuery:Enabled` both default to `false` in the property, while their
committed `appsettings.json` values turn them **on** for development — a convenience each of those
features' own docs are careful to call an environment's own choice, not a rule (see
[`sheet-query.md` §10.6](../documents/sheet-query.md#106-the-default-is-the-property-not-the-committed-file)).
`FileAgent:Enabled` inverts that: the committed value is off **too**, on purpose, because every run this
flag permits provisions a billed sandbox session — a cost neither sibling carries.
[`FileAgentOptionsTests.Bind_TheShippedConfiguration_LeavesFileGenerationOff`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/FileAgentOptionsTests.cs)
is what makes that a rule rather than an accident: unlike `SheetQueryOptionsTests`, which pins only that
an unconfigured environment gets nothing — never the committed value itself, since an explicit setting is
not a default — this test binds the actual shipped file and fails if `true` is ever reintroduced. A
developer who wants to exercise the agent locally sets `FileAgent:Enabled` explicitly, in user secrets or
a personal override; the repository will not hand it to them by checkout.

## 14. Testing

**Unit** (`Enterprise.Gpt.Unit.Test.Agents`, plus the settings, service and enum suites named below):

- `FileAgentSkillsTests` — skill discovery against the real build output, topic-based selection, that two
  runs get distinct providers, that the one registered script runner refuses, and a code search
  confirming no `Azure.AI.Projects`/`AIProjectClient` construction exists anywhere in the solution.
- `GeneratedArtifactVerifierTests` — a passing and a corrupt fixture per format, built in the test rather
  than committed, plus zero bytes and an extension this platform does not produce.
- `FileAgentPreflightTests` — **every published matrix cell**, asserting a `refused` pair refuses and
  every other tier proceeds; plus ambiguity, an unknown source, and that the three refusals carry three
  different sub-statuses.
- `FileAgentFailuresTests` — the four failures' wording, and that all seven lines a card can show (the
  four failures plus the three refusals) read as seven distinct strings.
- `ConversionMatrixTests` / `ConversionMatrixDocumentTests` — the loader against the JSON, and the JSON
  against both rendered tables, so the skill and the code cannot disagree about a pair.
- `FileAgentDocumentReaderTests`, `FileAgentFormatsTests`, `FileAgentQuotaServiceTests`.
- `FileAgentOptionsTests` — binds the real, committed `appsettings.json` and asserts `Enabled` reads
  `false` (§13.6), that the shipped bounds validate, and that every numeric range still rejects a value
  outside it.
- `ConversationServiceTests` — the ceiling stand-down, the discard on an abandoned turn, that a nested
  scope reaches the stream as a `depth: 2` child, the end-to-end token attribution that fails on a wrong
  `trackUsage` in either direction, and (§12) that a caller without the `Generate Files` grant is never
  asked for the tool, that an administrator holds no implicit grant, and that revoking the grant between
  two turns in the same conversation stands the tool down on the second without touching the first.
- `PermissionServiceTests` — `Generate Files` rejects a rename and a deactivation the same way
  `Administrator` and `Upload File` already do (§12).
- `PermissionIdsTests` — that every built-in id has a `Names` entry, and that each entry matches its
  seeded row. The first is what lets §12's built-in guard read `Names` instead of one `if` per id: only
  ids named in an endpoint filter are forced into that map at startup, and this permission is read from
  a service rather than a filter.

**Integration** — the `(ModelId, DateCreated)` index against real SQL Server; that a discarded document
leaves no live row and no blob; that downloading an already-generated document still succeeds with
`FileAgent:Enabled` off (§13.4); and, on the seeded `Generate Files` permission (§12), that it is present
and not granted by default, that renaming or deactivating it is rejected, and that granting it to a user
reaches their own `GET api/users/me` permission list.

**Opt-in and billable**, neither run in CI: the `FileAgentSpike` suite proves the raw SDK mechanism
against a live deployment ([`sandbox-capabilities.md`](sandbox-capabilities.md) §3), and the
`FileAgentBenchmark` suite runs the thirty-prompt benchmark through the agent's own instructions and the
production verifier. Each has its own opt-in key, so enabling one does not enable the other.

## 15. Key files

| Concern | File |
| --- | --- |
| Settings | [`Enterprise.Gpt.Service/Settings/FileAgentOptions.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Settings/FileAgentOptions.cs) |
| Model resolution | [`Enterprise.Gpt.Service/Agents/FileAgentModelResolver.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentModelResolver.cs) |
| Startup validation | [`Enterprise.Gpt.Api/Startup/FileAgentBootstrapper.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Startup/FileAgentBootstrapper.cs) |
| The dedicated client and shared `OpenAIClient` | [`Enterprise.Gpt.Api/Program.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Program.cs) |
| The tool contracts | [`Enterprise.Gpt.Service/Agents/FileAgentToolProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentToolProvider.cs) (`IFileAgentToolProvider`/`IFileAgentToolLease`) |
| The composition | [`Enterprise.Gpt.Api/Agents/FileAgentToolProvider.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentToolProvider.cs) |
| Mounting and harvesting | [`Enterprise.Gpt.Api/Agents/FileAgentSandbox.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentSandbox.cs) |
| Source-file resolution | [`Enterprise.Gpt.Service/Agents/FileAgentDocumentReader.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentDocumentReader.cs) |
| Skills | [`Enterprise.Gpt.Api/Agents/FileAgentSkills.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentSkills.cs), [`Enterprise.Gpt.Service/Agents/Documents/Skills/`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/Documents/Skills/) |
| Instructions | [`Enterprise.Gpt.Service/Prompts/file-agent-instructions.md`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Prompts/file-agent-instructions.md) |
| Tool naming | [`Enterprise.Gpt.Service/Agents/FileAgentToolNames.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentToolNames.cs) |
| Attachment to the turn | [`Enterprise.Gpt.Service/ConversationService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/ConversationService.cs) (`CreateChatOptionsAsync`) |
| Outcomes | [`Enterprise.Gpt.Service/Agents/FileAgentOutcomes.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentOutcomes.cs) |
| Failure wording | [`Enterprise.Gpt.Service/Agents/FileAgentFailures.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentFailures.cs) |
| Pre-flight refusals | [`Enterprise.Gpt.Service/Agents/FileAgentPreflight.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentPreflight.cs), [`FileAgentFormats.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentFormats.cs) |
| The confirmed matrix, read by code | [`Enterprise.Gpt.Service/Agents/ConversionMatrix.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/ConversionMatrix.cs) |
| Verification | [`Enterprise.Gpt.Service/Agents/GeneratedArtifactVerifier.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/GeneratedArtifactVerifier.cs) |
| The per-user ceiling | [`Enterprise.Gpt.Service/Agents/FileAgentQuotaService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentQuotaService.cs) |
| Telemetry | [`Enterprise.Gpt.Service/Observability/ChatMetrics.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/ChatMetrics.cs), [`FileAgentTracing.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Observability/FileAgentTracing.cs) |
| The permission (§12) | [`Enterprise.Gpt.Dto/Enums/PermissionIds.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Dto/Enums/PermissionIds.cs) (`GenerateFiles`), [`Enterprise.Gpt.Repository/Configurations/PermissionConfiguration.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Configurations/PermissionConfiguration.cs), the seeding migration [`20260828214951_SeedGenerateFilesPermission.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Repository/Migrations/20260828214951_SeedGenerateFilesPermission.cs), [`Enterprise.Gpt.Service/PermissionService.cs`](../../enterprise-gpt-api/Enterprise.Gpt.Service/PermissionService.cs) (`EnsurePermissionIsCustom`) |
| Rollback (§13) | [`Enterprise.Gpt.Api/appsettings.json`](../../enterprise-gpt-api/Enterprise.Gpt.Api/appsettings.json), [`tests/Enterprise.Gpt.Unit.Test/Settings/FileAgentOptionsTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Settings/FileAgentOptionsTests.cs) |
