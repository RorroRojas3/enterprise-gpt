# File Agent

The `file_agent` tool: a nested agent that writes and runs Python inside a hosted sandbox to produce
a document, then hands the artifact back to the turn.

## Gating

Three things must all be true before the tool is offered:

1. `FileAgent:Enabled` is true. This is the feature's rollback lever — off, the tool is never
   attached and no sandbox session starts. A file already generated stays downloadable; this governs
   new generation, not access to what exists. The committed `appsettings.json` ships **false**,
   unlike the other feature flags, because this is the one that bills per run.
2. The selected model supports tools.
3. The caller holds **`Generate Files`** (`PermissionIds.GenerateFiles`), the third built-in
   permission. Its `IsDefault` is **false**, unlike `Upload File`: every run provisions a billed
   sandbox session, so it is granted deliberately rather than handed to everyone at sign-in.

## Configuration — `FileAgent`

| Setting | Default | Range | Governs |
| --- | --- | --- | --- |
| `Enabled` | `false` | — | Whether the tool is offered at all |
| `ModelId` | required | non-empty guid | The `Core.Ref.Model` row the agent runs on |
| `ToolTimeoutSeconds` | 300 | 30–1800 | Wall clock for one `file_agent` call, independent of the outer turn |
| `MaxArtifactsPerRun` | 3 | 1–10 | Artifacts one run may persist; the surplus is dropped and reported rather than failing a run that also produced what was asked for |
| `MaxIterationsPerRun` | 12 | 2–40 | The agent's own iteration ceiling |
| `MaxVerificationRetries` | 1 | 0–3 | Regenerations of an artifact that failed its check; zero is legitimate |
| `MaxRunsPerUserPerDay` | unset | 1–10000 | Rolling-day run ceiling per user |
| `MaxSandboxSecondsPerUserPerDay` | unset | 1–1000000 | Rolling-day sandbox seconds per user |

`ModelId` is checked explicitly non-empty, because a `Guid` has no natural "unset" `[Range]`.

The pinned catalog row is validated at startup by `FileAgentBootstrapper` and seeded by its own
migration.

## Its own chat client

The agent does not share `ChatClientKeys.AzureOpenAI`. It gets a keyed registration of its own over
the identical Responses route, with two deliberate differences.

**Its own function-invocation bounds.** `MaximumIterationsPerRequest` (bound to
`MaxIterationsPerRun`), `AllowConcurrentInvocation = false`, `IncludeDetailedErrors = true`,
`MaximumConsecutiveErrorsPerRequest = 5`. These have to live here:
`MaximumIterationsPerRequest` is an **instance** setting with no per-request override, so sharing the
turn's client would silently cap the agent at the outer turn's ceiling of 5 — nowhere near enough for
load-skill, read-reference, run-code, re-open-and-check.

**No `.UseToolTracking(...)`.** A nested tool tracker would open its own root scope with no writer
attached, swallowing every progress line the run reports and putting its child activities out of
reach of the turn's stream. Leaving tracking off keeps the ambient activity scope pointing at the
enclosing `file_agent` call, which is what lets the agent's steps nest underneath it in the timeline.

`ChatClientKeys.FileAgent` is **not a provider key** — no `Core.Ref.Provider` row maps to it and
`Providers.ServiceKeys` never resolves it. It exists purely because those two differences are client
*instance* settings.

`IHostedFileClient` is built from `OpenAIClient` directly rather than through the narrower
`GetFileClient()` overload, which cannot see a file the sandbox wrote into its own container and
answers 404 for every artifact.

## Skills

Nine skills ship under `Service/Agents/Documents/Skills/`, one `SKILL.md` per directory:
`docx-authoring`, `xlsx-authoring`, `pptx-authoring`, `pdf-authoring`, `csv-tabular`,
`markdown-text`, `document-comparison`, `document-conversion`, `artifact-verification`.
`FileAgentSkills.Discover` fails loudly if the deployed set does not match what the code expects,
both at startup and in a test pinning the exact nine names.

Two stages of trimming, and either alone is only half the mechanism:

- **Advertise-stage** keeps a run from spending tokens on a skill's advertisement when it cannot use
  it.
- **Load-stage** — progressive disclosure through `load_skill`, `read_skill_resource`,
  `run_skill_script` — keeps a skill's body out of context until the model asks for it.

`FileAgentSkills.Select(instruction)` matches a static topic table: "docx"/"word"/"document" implies
`docx-authoring`, "compare"/"diff"/"changed" implies `document-comparison`, and so on.
`artifact-verification` rides along with any match, since every run should re-check its own output.
An instruction matching no topic advertises **everything** — the safe direction to fail, since an
unadvertised skill is one the model has no way to ask for.

Matching is on **whole words**, not substrings: `"md"` occurs inside "command" and `"text"` inside
"context".

### The filter reads an instruction that does not exist yet

The skills provider is built during tool assembly, **before the model has produced any output** — it
has not decided to call `file_agent`, let alone said what it wants built. So the provider is handed a
function rather than a string:

```csharp
var instruction = new RunInstruction();
FileAgentSkills.CreateProvider(FileAgentSkills.DeployedRoot, () => instruction.Text, _loggerFactory)
```

`FileAgentRun.RunAsync` sets `instruction.Text` from the run's own messages before calling the inner
agent, and the filter — which runs when the provider is consulted, not when it is built — reads it
then. Skill caching is switched off for the same reason: a cached advertisement would be the first
call's answer whatever the second asked for, and one turn can call the agent twice.

The provider is built fresh per turn, never cached across turns; a shared one would leak one
conversation's formats into another's advertisement.

## Script execution stays off the API host

Every line of Python runs inside the hosted sandbox, never as a subprocess on the API host. Three
things enforce that together:

1. **`AllowedScriptExtensions = []`** — no file is ever discovered as a runnable script, whatever a
   skill's directory contains.
2. **`run_skill_script` keeps its approval requirement.** Nothing in a headless server turn can
   answer an approval request, so that path stalls rather than executing.
3. **Exactly one `AgentFileSkillScriptRunner` is registered anywhere, and it throws.** "Zero call
   sites" is not achievable — the package refuses to construct a file-skill provider with no runner
   registered at all. A refusing runner is the strongest guard available, and stricter than absence,
   because it fails loudly rather than by omission. A code-search test pins that this is the only
   call site, so a second, real runner added later fails the build.

The two read-only skill tools have their approval requirement disabled; `run_skill_script` does not.

## Inputs and artifacts

The tool's outward schema is one natural-language string, so there is no structured
`sourceDocumentNames` parameter — source resolution happens inside the agent's pipeline, ahead of the
model seeing the request. Resolved files are uploaded into the sandbox; produced files are harvested
back out through `IHostedFileClient`.

A harvested artifact is verified by `GeneratedArtifactVerifier` before it is persisted, and a failed
check may be regenerated up to `MaxVerificationRetries` times.

Persistence and delivery — the second blob container, the `Generated` discriminator, withdrawal of
undelivered files — are covered in [../documents/downloads.md](../documents/downloads.md).

## The conversion matrix

`Service/Agents/Documents/conversion-matrix.json` is the authority on which format conversions the
sandbox can perform. The table below is **rendered from it**, and
`ConversionMatrixDocumentTests` fails if the two drift apart — a JSON file alone does not make a
single source, because the moment someone renders it into prose, prose is what the next reader
believes.

| Tier | Meaning |
| --- | --- |
| `✓` faithful | The target carries everything the source expressed that the format can hold. No caveat needed. |
| `◐` structural | Content, heading hierarchy, tables, lists and images survive; exact pagination, typography and vendor-specific layout do not. The answer states what was lost. A real conversion, not a refusal. |
| `refused` | No path in this sandbox produces a result worth handing a user. Refused by name before any run starts. |
| `n/a` | Not offered, because no natural request maps to it. Not a refusal. |

<!-- conversion-matrix:begin -->
Status: confirmed. Recorded: 2026-08-28. Deployment: rr-gpt-5.6-luna. Office converter: absent.

| From \ To | docx | xlsx | pptx | pdf | csv | md | txt |
| --- | --- | --- | --- | --- | --- | --- | --- |
| docx | — | n/a | n/a | ◐ | n/a | ✓ | ✓ |
| xlsx | n/a | — | n/a | ◐ | ✓ | ✓ | ✓ |
| pptx | n/a | n/a | — | ◐ | n/a | ✓ | ✓ |
| pdf | ◐ | ◐ | refused | — | ◐ | ◐ | ◐ |
| csv | ✓ | ✓ | n/a | ◐ | — | ✓ | ✓ |
| md | ✓ | n/a | n/a | ✓ | n/a | — | ✓ |
| txt | ✓ | n/a | n/a | ✓ | n/a | ✓ | — |
<!-- conversion-matrix:end -->

Each cell in the JSON carries the engine that serves it, the note the answer must include, the
proposed tier, and — once recorded — the evidence of the attempt that settled it. A pair proposed as
`refused` is still attempted; the recorded attempt is what makes the refusal evidence rather than an
assumption.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentSandbox.cs` | Sandbox session, uploads and downloads |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentSkills.cs` | Discovery, the refusing script runner |
| `enterprise-gpt-api/Enterprise.Gpt.Api/Agents/FileAgentToolProvider.cs` | Per-turn composition |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Agents/FileAgentQuotaService.cs` | Rolling-day ceilings |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Agents/GeneratedArtifactVerifier.cs` | Post-run checks |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Agents/ConversionMatrix.cs` | What the sandbox can convert |
| `enterprise-gpt-api/Enterprise.Gpt.Service/Settings/FileAgentOptions.cs` | The options above |
| `enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Agents/` | Skill selection, the single-runner guard |

## Related

- [../documents/downloads.md](../documents/downloads.md)
- [../architecture/auth-and-permissions.md](../architecture/auth-and-permissions.md)
- [../models/catalog.md](../models/catalog.md)
