# The File Agent

Enterprise GPT's assistant can now hand off "make me a spreadsheet of these numbers" to a specialised
agent that writes and runs Python in a hosted sandbox to actually produce the file, instead of pasting a
Markdown table into a chat bubble. This document covers how that agent is put together: its own model
and settings, the client it runs on, how a file gets in and a produced artifact gets back out through an
SDK bridge that was not built to carry either, its skills, and the guarantees that keep every line of
Python it runs inside the sandbox.

**Scope.** This is Wave 1 — the agent's composition, its model, its skills, and the mount/harvest
mechanism. It does **not** cover what the agent is actually asked to do: no deterministic verification
pass exists yet, none of the four verbs (create, edit, compare, convert) has its own tested behaviour,
and a run that answers with prose and no file is not yet told apart from one that succeeded. See §12 for
the full list of what is not built. For where a produced file is stored and delivered, see
[`generated-files.md`](generated-files.md); for what the sandbox itself can and cannot do, see
[`sandbox-capabilities.md`](sandbox-capabilities.md).

## 1. `FileAgentOptions`, and every setting

Bound from the `FileAgent` configuration section
(`Enterprise.Gpt.Service/Settings/FileAgentOptions.cs`), validated with
`AddOptions<T>().Bind(...).ValidateDataAnnotations().Validate(...).ValidateOnStart()` — the same shape
`SummarizationOptions` uses.

| Setting | Default | Range | What it governs |
| --- | --- | --- | --- |
| `Enabled` | `false` | — | Whether the tool is offered to the model at all. This is the feature's whole rollback lever: off, the tool is never attached and no sandbox session ever starts. A file already generated stays downloadable regardless — this governs new generation, not access to what exists. |
| `ModelId` | *(required)* | non-empty `Guid` | The `Core.Ref.Model` row the agent runs on. See §2. |
| `ToolTimeoutSeconds` | `300` | 30–1800 | The wall-clock ceiling on one `file_agent` call, independent of the outer turn's own bounds — see §4. |
| `MaxArtifactsPerRun` | `3` | 1–10 | How many artifacts one run may persist; the surplus is dropped and reported rather than failing a run that also produced what was asked for. |
| `MaxIterationsPerRun` | `12` | 2–40 | The agent's own `MaximumIterationsPerRequest`, set on its dedicated client (§4) — its own number because the agent spends iterations the outer turn does not (load a skill, read its reference, run the code, re-open the artifact). |

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

`ConversationService` attaches the tool through the same gate ladder `document_summarize` already climbs
— minus the permission rung, which is a later wave (§12) — and treats a stood-down agent as a lost
capability, not a failed turn: `FileAgent:Enabled` off, a model with no tool support, or a name collision
with a user's own MCP selection each simply skip attaching it, logged at warning. A composition failure
(the model row misconfigured, the client missing) is caught and logged at error rather than failing the
turn, on the theory that the startup validator (§2) is where that should already have surfaced.

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
verbatim. A run that stored nothing gets an explicit `"[No file was saved. ... Do not describe a file as
though the user has one.]"` appended, because the agent legitimately produces no file sometimes (an
unsupported conversion, an ambiguous source name) and turning that into a thrown exception would make a
correct refusal look like a broken tool call. Telling a *genuine* failure apart from a legitimate refusal
is a separate concern this wave does not yet build (§12, US-203).

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
retrieval must never see one at all (see [`generated-files.md`](generated-files.md) §5). Both apply the
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

## 12. What Wave 1 does not build yet

This document describes the mechanism, not the product. None of the following exists yet — do not assume
any of it when reading the code:

- **The four verbs' own behaviour.** Today the agent has only its generic instructions
  (`file-agent-instructions.md`) and whatever the model attempts unprompted; create, edit, compare and
  convert have no dedicated logic or tests of their own (EP-4).
- **A deterministic verification pass.** Nothing re-opens a produced artifact and checks it matches the
  requested shape before it is stored.
- **"No file produced" as a named, distinct failure.** A run that answers with prose and no artifact is
  not yet told apart from one that genuinely completed.
- **Stop-cancellation cleanup.** Pressing Stop mid-run is not yet wired to abandon the sandbox call,
  release the lock promptly, or clean up a partial artifact.
- **Usage attribution.** `UsageReportTranslator`'s `ModelId`/`DeploymentName` nulls are still unfilled for
  an agent tool-call row, no sandbox-seconds metric exists, and there is no per-user spend ceiling —
  the setting for one is deliberately absent rather than bound-and-ignored, because a documented cost
  control nothing enforces is worse than none.
- **The agent's own activity-card states.** Nested activities reach the stream structurally through
  `ChatProgress`, but no dedicated states, failure copy, or accessibility pass has shipped for them.
- **The permission gate.** The feature is reachable by anyone once `FileAgent:Enabled` is on; the
  dedicated grant arrives in a later wave, which is also why the flag defaults off.

## 13. Testing

**Unit** (`Enterprise.Gpt.Unit.Test.Agents.FileAgentSkillsTests`): skill discovery against the real build
output, topic-based selection (including that an unmatched instruction advertises everything and a
matched one leaves the others out), that two runs get distinct provider instances, that the one
registered script runner refuses when invoked, and a code-search guard confirming no
`Azure.AI.Projects`/`AIProjectClient` construction exists anywhere in the solution.

The opt-in, billable `FileAgentSpike` suite (`tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/`)
predates this composition — it proves the raw SDK mechanism against a live deployment
([`sandbox-capabilities.md`](sandbox-capabilities.md) §3) and does not exercise `FileAgentToolProvider`
or any of the composition described above.

## 14. Key files

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
