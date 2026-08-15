# Build order: Image Tool

A dependency-resolved execution sequence for the 31 stories in [`image-tool.md`](image-tool.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives five waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: waves 1 through 3 each contain internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave. Within a wave, the `Parallel with` column names the stories that neither depend on it nor are depended on by it — those can be taken concurrently by however many people are available. Because wave 1 splits into two lanes that never touch each other, that column uses lane shorthand there rather than enumerating the ids on every row.

Status here mirrors the `Status` field on each PRD story. Update both, or update the PRD and re-derive this.

**Progress: 0 / 31 done.**

**Critical path.** `US-001` (confirm the deployment) → `US-002` (prove the edit surface) → `US-201` (client, options and flag) → `US-202` (the `generate_image` function) → `US-302` (resolve and supply source images) → `US-303` (edit an image in the conversation) → `US-304` (combine or vary several), seven deep, with `US-101` → `US-301` joining ahead of `US-302`. Every link of it runs through the generation lane or the epic that consumes it, which is why §8 spends schedule pressure there and treats the storage lane as the place to put anyone idle during the gate. **Maximum useful concurrency is 6**, in wave 4, where six stories carry no edge between them at all; wave 1 by contrast opens with only three ready stories despite holding eight.

## Wave 0 — the gate

Two stories, no dependencies beyond each other, and nothing below is estimated in detail until both close. The `gpt-image-1` series is limited-access while `gpt-image-2` is generally available, the deployment's quota is a **request** rate rather than a token rate, and `/images/edits` is a `multipart/form-data` surface distinct from `/images/generations`.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | US-001 `[enabler]` Confirm the image deployment | — | P0 | — | |
| 0 | 2 | US-002 `[enabler]` Prove the edit surface from .NET | US-001 | P0 | — | |

⛔ **This is a gate, not a phase.** US-002 pins the package ids and versions, establishes whether the installed client libraries expose the image **array** form or a typed `HttpClient` is required, records whether `usage` is returned on the edits path as well as generations, and settles the per-image size ceiling — the how-to page says 50 MB and the v1-preview reference says 25 MB for the same endpoint. US-201 writes a registration around whatever it finds; starting US-201 first means writing it twice.

## Wave 1 — two parallel lanes

Two lanes with no edge between them: **A** = storage (EP-1's US-101 … US-106), **B** = generation (EP-2's US-201 and US-202). Lane A depends on no Azure capability at all and can start during wave 0 — it is the natural home for anyone idle at the gate.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 3 | US-101 `[enabler]` Add the document-type discriminator and migrate existing rows | — | P0 | US-102, all of lane B | |
| 1 | 4 | US-102 `[enabler]` Provision the `generated` container and its configuration | — | P0 | US-101, all of lane B | |
| 1 | 5 | US-201 `[enabler]` Register the image client, options and flag | US-002 | P0 | all of lane A | |
| 1 | 6 | US-103 `[enabler]` Persist a generated file without ingesting it | US-101, US-102 | P0 | all of lane B | |
| 1 | 7 | US-202 `[enabler]` Build the `generate_image` function with real usage reporting | US-201 | P0 | all of lane A | |
| 1 | 8 | US-104 Download a generated file in its own format | US-103 | P1 | US-105, US-106, all of lane B | |
| 1 | 9 | US-105 `[enabler]` Carry the generated-file reference on the assistant message | US-103 | P0 | US-104, US-106, all of lane B | |
| 1 | 10 | US-106 A generated or attached file is never retrieved or cited | US-103 | P0 | US-104, US-105, all of lane B | |

⚠ **`Program.cs` and the tool funnel are the collision.** US-201 adds a registration block to `Program.cs`; US-203, in wave 2, adds the first new tool to `CreateChatOptionsAsync` — the single funnel that builds one `List<AITool>` at line 1151 and assigns `chatOptions.Tools` exactly once at line 1253. The PRD's resolution is to sequence US-203 into wave 2 so the registration block settles first and the funnel gains its first new tool once, from one hand. Nothing else in this wave touches either file.

## Wave 2 — attachment, batching and the first frontend surface

Two lanes again, and neither waits on the other. The generation lane puts the tool through the funnel and then batches it; the frontend lane needs only US-105's contract and is testable against a hand-inserted `Generated` row with no image deployment in existence.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 11 | US-203 Ask the assistant for an image | US-202 | P0 | US-401, US-402, US-403 | |
| 2 | 12 | US-401 See the images the assistant made | US-105 | P0 | US-203, US-204, US-205, US-206 | |
| 2 | 13 | US-204 Ask for several images at once | US-203 | P1 | US-205, US-206, US-401, US-402, US-403 | |
| 2 | 14 | US-205 Keep a generated image after the turn ends | US-203, US-103 | P1 | US-204, US-206, US-401, US-402, US-403 | |
| 2 | 15 | US-206 Image generation fails without taking the turn with it | US-203 | P1 | US-204, US-205, US-401, US-402, US-403 | |
| 2 | 16 | US-402 Open an image full size and download it | US-401, US-104 | P1 | US-403, US-203, US-204, US-205, US-206 | |
| 2 | 17 | US-403 Images survive a reload | US-401 | P1 | US-402, US-203, US-204, US-205, US-206 | |

⛔ **Open question 1 gates US-401.** How an inline preview gets its bytes is unresolved: the download route mints a five-minute SAS, and the standing posture is that a signed link is never prefetched, persisted or written into a URL. The PRD proposes a new authenticated byte-streaming route for previews with the SAS route kept for explicit downloads. Starting US-401 before that answer lands risks building the one thing US-401's own acceptance criterion forbids.

## Wave 3 — image input and editing

The upload path and the composer's picker are **one contract with two ends** and are scheduled in the same wave for that reason. US-405 is the only story here whose dependencies are entirely in wave 2, so it is a second root and the natural first pick for whoever is not on US-301.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 18 | US-301 `[enabler]` Accept an image upload without ingesting it | US-101 | P0 | US-405 | |
| 3 | 19 | US-405 Loading and failure states for the image surface | US-401, US-206 | P1 | all of wave 3 | |
| 3 | 20 | US-302 `[enabler]` Resolve and supply source images to the edit call | US-301, US-202 | P0 | US-404, US-405, US-406 | |
| 3 | 21 | US-404 Attach an image in the composer | US-301 | P0 | US-302, US-303, US-304, US-305, US-405 | |
| 3 | 22 | US-303 Edit an image already in the conversation | US-302 | P0 | US-305, US-404, US-405, US-406 | |
| 3 | 23 | US-305 An unusable source image is refused clearly | US-302 | P1 | US-303, US-304, US-404, US-405, US-406 | |
| 3 | 24 | US-406 Use the image surface without a mouse | US-401, US-404 | P0 | US-302, US-303, US-304, US-305, US-405 | |
| 3 | 25 | US-304 Combine or vary several source images at once | US-303 | P1 | US-305, US-404, US-405, US-406 | |

⚠ **US-301 and US-404 are two ends of one contract.** `DocumentEndpoints.GetFileExtensions` is derived from the registered extractors, and the client's root-scoped `SupportedExtensionsStore` reads that route to decide what the picker offers. An image the validator accepts but the endpoint does not advertise is unreachable from the UI, and an extension the picker offers that the validator refuses is a round trip to a 400. Whoever takes the two should coordinate directly, or take both.

⛔ **Open question 5 gates US-301.** Whether an uploaded image appears in the conversation's user-facing document list changes US-301's response shape and the composer's chip behaviour. It is a one-sentence answer and a rework if it arrives late.

## Wave 4 — governance and hardening

Nothing here has an edge to anything else here. All six can be taken concurrently, and none of them may be skipped before the feature is enabled for real users — the flag defaults off precisely so that this wave is not optional.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 26 | US-504 Gate image generation on a permission | US-203 | P0 | US-505, US-501, US-502, US-503, US-107 | |
| 4 | 27 | US-505 Feature-flag the capability with a documented rollback | US-203 | P0 | US-504, US-501, US-502, US-503, US-107 | |
| 4 | 28 | US-501 Image tokens and image counts are attributed to the turn | US-202 | P1 | US-504, US-505, US-502, US-503, US-107 | |
| 4 | 29 | US-502 Emit image spans and metrics | US-202 | P1 | US-504, US-505, US-501, US-503, US-107 | |
| 4 | 30 | US-503 Bound what one turn and one user can generate | US-202 | P1 | US-504, US-505, US-501, US-502, US-107 | |
| 4 | 31 | US-107 Reclaim generated blobs under a retention policy | US-102 | P2 | US-504, US-505, US-501, US-502, US-503 | |

Three of these six are blocked on an answer rather than on code: US-503 on open question 2 (the ceilings), US-504 on open question 3 (which permission), US-107 on open question 4 (the retention window). Getting those three answers during wave 3 is what keeps this wave one week rather than three. US-502 is not optional despite being P1 — `image.generation.duration`, `image.generation.count` and `image.persisted.count` are the only sources the PRD's latency and batch-fidelity success criteria have, and without it two of the five criteria are unmeasurable.
