# File Agent Sandbox Capabilities

Findings from EP-0, the capability gate that establishes what Azure OpenAI's hosted Code Interpreter
can actually do on this deployment before any conversion code is built on top of it. Audience:
engineers building EP-2 through EP-4 of the File Agent, and anyone deciding whether a document
conversion request is safe to promise.

## 1. Overview

The File Agent ([PRD](../prd/file-agent/file-agent.md)) produces documents by writing and running
Python inside a hosted Code Interpreter sandbox, on the Azure OpenAI **Responses** route this
application already registers as `ChatClientKeys.AzureOpenAI` (`Program.cs:157-191`). Everything the
feature can promise — which formats it authors, which conversions it serves and at what fidelity —
is a property of that sandbox image rather than of any code in this repository.

EP-0 is the gate that establishes those properties against the real deployment rather than against
documentation about a different one. This document is where its findings are published.

| Story | Question | Where the answer lives |
| --- | --- | --- |
| US-001 | Does Code Interpreter run on this route, and how do files go in and artifacts come out? | `FileAgentSpike/Evidence/capability-report.json` |
| US-002 | Which Python libraries does the image carry, and is there a `soffice` binary? | `FileAgentSpike/Evidence/sandbox-inventory.json` |
| US-003 | Which conversion pairs are achievable, at which fidelity tier? | [`conversion-matrix.json`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/Documents/conversion-matrix.json) |

The matrix is deliberately **not** kept here as prose. §5 renders a table out of that JSON file, and
a unit test fails the build if the two disagree — EP-3's `document-conversion` skill and EP-4's
`FileAgentOptions` read the same file, so a pair cannot be refused in one place and offered in
another.

> **Status.** The harness, the evidence files and the guards are in place; the recording run against
> a live deployment has not been made yet. Until it has, every tier in §5 is the PRD's *proposal*,
> marked as such by the matrix's own `status` field, and no cell carries evidence.

## 2. How a file reaches the sandbox, and how an artifact comes back

This is the part of the design the PRD gets wrong, and the reason US-001 exists.

`HostedCodeInterpreterTool.Inputs` accepts any `AIContent`, and its own documentation says
"unsupported inputs will be ignored by the `IChatClient` to which the tool is passed." In
`Microsoft.Extensions.AI.OpenAI` 10.9.0 the Responses bridge maps the tool like this:

```csharp
case HostedCodeInterpreterTool codeTool:
    return new CodeInterpreterTool(
        new(codeTool.Inputs?.OfType<HostedFileContent>().Select(f => f.FileId).ToList() is { Count: > 0 } ids ?
            CodeInterpreterToolContainerConfiguration.CreateAutomaticContainerConfiguration(ids) :
            new()));
```

Only `HostedFileContent` survives. **`DataContent` — raw bytes — is silently discarded**, which is
what FR-10 and US-202 currently describe as the input mechanism. The working path is one step longer:

1. Upload the bytes to the Files API, producing a file id. `OpenAIClient.AsIHostedFileClient()`
   returns an `IHostedFileClient` that speaks both the standard Files API and container-scoped files.
2. Put `new HostedFileContent(fileId)` on `HostedCodeInterpreterTool.Inputs`. Azure mounts it in the
   container at `/mnt/data/{file-id}-{original-filename}`.
3. Read produced artifacts back off the response. A produced file arrives as a `HostedFileContent`
   whose `Scope` is the **container** id, not as bytes.
4. Download it with that scope set:
   `IHostedFileClient.DownloadAsync(fileId, new() { Scope = containerId })`, which resolves to
   `GET {endpoint}/openai/v1/containers/{container}/files/{file}/content`. Without the scope the call
   resolves against the standard Files API, which cannot see a file the code interpreter wrote, and
   answers `404`.

`CodeInterpreterToolResultContent.Outputs` and the message's own `CitationAnnotation`s are both
plausible carriers for step 3 — the SDK implies the first, Azure's documentation implies the second.
The harness looks on every channel and records which one answered, rather than picking one; that
recorded answer is what FR-11 should be rewritten against.

Two constraints follow from the same reading, and they shape the cost of everything above:

- A container is created per response and reused only while a previous code interpreter call stays in
  the model's context. It expires after **20 minutes** idle.
- A session is billed **per minute with a five-minute minimum**, on top of tokens. A run that starts
  a fresh conversation per question pays a fresh minimum per question.

## 3. Running the capability gate

The gate lives at
[`tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike),
as skipped integration tests rather than a throwaway console app, so a future re-run after an Azure
image change can diff against what this run recorded.

It is **opt-in**. Credentials alone do not enable it: the machine that runs the API already has
`AzureOpenAI:ApiKey` in user secrets, and a suite that started spending the moment those existed
would bill anyone who typed `dotnet test`. Both switches are required.

```bash
# One-time, on the machine that will run it. The id is the API project's own UserSecretsId.
dotnet user-secrets --id 27ef94e5-8a02-48e5-b431-caa1880f8198 set "FileAgentSpike:Enabled" "true"
dotnet user-secrets --id 27ef94e5-8a02-48e5-b431-caa1880f8198 set "FileAgentSpike:Record"  "true"

cd enterprise-gpt-api
dotnet test tests/Enterprise.Gpt.Integration.Test/Enterprise.Gpt.Integration.Test.csproj \
  --filter "FullyQualifiedName~FileAgentSpike"
```

| Setting | Default | Effect |
| --- | --- | --- |
| `FileAgentSpike:Enabled` | off | Off, every probe reports **Skipped** with the reason. This is the state CI is always in. |
| `FileAgentSpike:Record` | off | On, a run **overwrites** the evidence files and the conversion matrix in the source tree. Off, it diffs against them and fails naming what moved. |
| `FileAgentSpike:Model` | `AzureOpenAI:DefaultModel` | The deployment to probe, for the case US-301 anticipates where the File Agent is pinned to one of its own. |

After a recording run: review the emitted JSON, commit it, unset `Record`, and run once more. The
second run is what proves the evidence is reproducible rather than a single transcript.

Recording mode writes back into the source tree the assembly was compiled from, so it only works from
a checkout of this repository. Running it elsewhere fails with a message saying so, rather than
leaving evidence in a `bin` directory nobody will commit.

## 4. Python library inventory

Recorded by US-002 into `FileAgentSpike/Evidence/sandbox-inventory.json`, which every later run diffs
against. Presence and **major** version are compared: a patch bump is not a finding, a library
appearing or disappearing is, and so is `fpdf` turning out to be 1.x when a skill was written against
the 2.x API.

The PRD names these: `python-docx`, `openpyxl`, `python-pptx`, `pandas`, `reportlab`, `fpdf`,
`pypdf`, `pdfplumber`, `PyMuPDF`, `weasyprint`, `Pillow`, `markdown`. The probe adds `PyPDF2`,
`beautifulsoup4`, `lxml` and `matplotlib`, because a skill that needs one of them and finds it absent
is a story that stalls in EP-3.

The `soffice`/`libreoffice` probe is the single most consequential line in that file: a headless
office suite promotes every Office → `pdf` cell in §5 from structural to faithful.

Nothing in the sandbox can install a missing library — that is the working assumption, and US-001
checks it on three separate paths (HTTPS, a raw socket, and `pip install`), failing loudly if any one
of them reaches the network. Once the recording run confirms all three are blocked, this inventory is
a fixed fact rather than a starting point.

## 5. Conversion matrix

The single source is
[`conversion-matrix.json`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/Documents/conversion-matrix.json).
The table below is rendered from it, and
[`ConversionMatrixDocumentTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Agents/ConversionMatrixDocumentTests.cs)
fails if the two drift apart.

Tiers, per the PRD:

- **`✓` faithful** — the target carries everything the source expressed that the format can hold. No
  caveat needed in the answer.
- **`◐` structural** — content, heading hierarchy, tables, lists and images survive; exact
  pagination, typography and vendor-specific layout do not. The answer states what was lost. This is
  a real conversion, not a refusal.
- **`refused`** — no path in this sandbox produces a result worth handing a user. Refused by name
  before any run starts.
- **`n/a`** — not offered in v1, because no natural request maps to it. Not a refusal.

<!-- conversion-matrix:begin -->
Status: confirmed. Recorded: 2026-08-27. Deployment: rr-gpt-5.6-luna. Office converter: absent.

| From \ To | docx | xlsx | pptx | pdf | csv | md | txt |
| --- | --- | --- | --- | --- | --- | --- | --- |
| docx | — | n/a | n/a | ◐ | n/a | ✓ | ✓ |
| xlsx | n/a | — | n/a | ◐ | ✓ | ✓ | ✓ |
| pptx | n/a | n/a | — | refused | n/a | ✓ | ✓ |
| pdf | ◐ | ◐ | refused | — | ◐ | ◐ | ◐ |
| csv | ✓ | ✓ | n/a | ◐ | — | ✓ | ✓ |
| md | ✓ | n/a | n/a | ✓ | n/a | — | ✓ |
| txt | ✓ | n/a | n/a | ✓ | n/a | ✓ | — |
<!-- conversion-matrix:end -->

Each cell in the JSON carries the engine that serves it, the note the answer has to include, the tier
the PRD proposed, and — once recorded — the evidence of the attempt that settled it. A pair proposed
as `refused` is still attempted; the recorded attempt is what makes the refusal evidence rather than
inheritance.

## 6. Known limits of this gate

- **The samples are authored in the sandbox, not by Office.** `make-samples.py` builds the seven
  source documents with the same libraries that read them back. That exercises every conversion path
  but not the messiness of a real `.docx` produced by Word, so a confirmed tier is a floor rather
  than a ceiling. The script is committed and deterministic, and US-002's inventory diff is what
  catches the library change that would silently alter what it produces.
- **A tier is graded down, never up, by automation.** The only promotion is structural → faithful,
  and only when a headless office suite actually performed the render. Promoting anything else is a
  human decision recorded in the matrix, not something a probe infers.
- **A filtered recording run records a partial capability report.** Deliberate: a run that quietly
  discarded findings because one probe was excluded would be worse than a file that visibly covers
  less than the whole story.
- **This is not a regression suite.** It is opt-in and billable, so nothing runs it on a schedule.
  The drift assertions exist for the day somebody deliberately re-runs it after an Azure change.

## 7. Key files

| Concern | File |
| --- | --- |
| The Responses-route client, built the way `Program.cs` builds it, plus upload, run and download | [`FileAgentSpike/SandboxSession.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/SandboxSession.cs) |
| Finds produced files on every channel that could carry one, and records which did | [`FileAgentSpike/ArtifactExtractor.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/ArtifactExtractor.cs) |
| US-001, one test per acceptance criterion | [`FileAgentSpike/CodeInterpreterReachabilityTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/CodeInterpreterReachabilityTests.cs) |
| US-002, and the fixture diff | [`FileAgentSpike/SandboxInventoryTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/SandboxInventoryTests.cs) |
| US-003, one pass down the source formats | [`FileAgentSpike/ConversionMatrixTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/ConversionMatrixTests.cs) |
| The probe programs, run verbatim and never paraphrased | [`FileAgentSpike/Scripts/`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Integration.Test/FileAgentSpike/Scripts) |
| The single source §5 renders | [`conversion-matrix.json`](../../enterprise-gpt-api/Enterprise.Gpt.Service/Agents/Documents/conversion-matrix.json) |
| The CI-safe guards: structure, tiers, evidence, and this document | [`ConversionMatrixDocumentTests.cs`](../../enterprise-gpt-api/tests/Enterprise.Gpt.Unit.Test/Agents/ConversionMatrixDocumentTests.cs) |
