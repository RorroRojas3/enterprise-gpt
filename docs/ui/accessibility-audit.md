# Accessibility audit

The automated axe-core run that gates the Angular client at `enterprise-gpt-ui/`: a second Vitest target that renders each route in a real headless browser, in both themes, and fails the build on any violation rated serious or critical.

Audience: a developer whose pull request has just gone red on `npm run test:a11y`, and anyone adding a route — because a route that is not in `AXE_ROUTES` is audited by nothing, and a jsdom spec now fails if you leave it out.

Companion to [Design System §8](design-system.md#8-accessibility-contracts) for the contracts this run measures, to [Responsive layout](responsive-layout.md) for the three widths it renders at, and to [Pull-request checks §7](../ci/pull-request-checks.md#7-ui-ci) for how it runs on CI. [The rebuild PRD](../prd/enterprise-ui-rebuild.md) is the authority for every `US-xxx` reference; bare `§` references are to sections of _this_ page.

## 1. Overview

US-1405 has four criteria and **three of them were already satisfied** when the story opened:

| Criterion                                                            | Status                                                                                                                                                                       |
| -------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| (a) an axe-core run over the routes, in both themes, failing the build | **This is the story.** §2–§5                                                                                                                                                 |
| (b) the initial chunk within budget, with the diagram and math libraries kept out of it | Already held since US-601 and US-605; budgets re-baselined here (§7)                                                                                       |
| (c) no view depending on a mutated plain object under `exhaustive` check-no-changes | Already held. The named failure mode — "upload progress and attachment chips" — is pinned by `upload-store.spec.ts` ("replaces records rather than mutating them") |
| (d) the US-107 Andes contract diff running in the same pipeline      | Already held: `Andes contract drift` is a job in `ui-ci.yml`                                                                                                                  |

So the work is one new test target, one harness, five audit specs, one coverage guard, two colour corrections that the run itself found, and two CI steps.

**The run found a real failure that three previous stories had missed**, and it found it only after the browser viewport was pinned to a desktop width (§6.2). That is the argument for the whole apparatus: everything else in `npm run lint` measures the source, and this measures what a browser actually paints.

### 1.1 Where each piece lives

| Concern                              | Where                                                                                                                                            |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| The target                           | [`angular.json`](../../enterprise-gpt-ui/angular.json) — `test-a11y`; [`package.json`](../../enterprise-gpt-ui/package.json) — `test:a11y`        |
| The harness                          | [`src/testing/a11y.ts`](../../enterprise-gpt-ui/src/testing/a11y.ts)                                                                              |
| The route record                     | [`src/testing/a11y-routes.ts`](../../enterprise-gpt-ui/src/testing/a11y-routes.ts)                                                                |
| The coverage guard (jsdom)           | [`src/testing/a11y-coverage.spec.ts`](../../enterprise-gpt-ui/src/testing/a11y-coverage.spec.ts)                                                  |
| The audits                           | `shell.a11y.spec.ts`, `chat.a11y.spec.ts`, `projects.a11y.spec.ts`, `admin.a11y.spec.ts`, `unavailable-panel.a11y.spec.ts`                        |
| The contrast table                   | [`scripts/check-tokens.mjs`](../../enterprise-gpt-ui/scripts/check-tokens.mjs) — `CONTRAST_PAIRS`                                                 |
| The colour source of record          | [`docs/design/project/theme.css`](../design/project/theme.css), mirrored in [`src/styles/_tokens.scss`](../../enterprise-gpt-ui/src/styles/_tokens.scss) |
| CI                                   | [`.github/workflows/ui-ci.yml`](../../.github/workflows/ui-ci.yml) — the `Lint, test, build` job                                                  |

## 2. Quick start

```bash
# from enterprise-gpt-ui/
npx --no playwright install chromium   # once per machine
npm run test:a11y                      # 80 audits in headless Chromium
```

`pretest:a11y` runs `npm run assets` first, so the generated fonts and icon sprite exist — exactly as `pretest` does for `npm test`.

To audit a new route, add it to `AXE_ROUTES` **and** navigate to it from an audit spec in the same commit. The jsdom coverage guard fails otherwise (§5.2), and it will not accept a mention in a comment: it requires the route to be an argument to `open(…)` or `navigateByUrl(…)`.

To debug a failure, read the message. `expectNoSeriousViolations` prints the rule id, its impact, up to four offending selectors with axe's own `failureSummary`, and the help URL. On CI the run also uploads a screenshot per failed assertion (§8).

## 3. Why a real browser

**A jsdom axe run is worse than no axe run.** Roughly half of what axe checks is computed style — colour contrast, whether an element is visible, whether a target is large enough — and jsdom has no cascade, no layout and no computed colour. It reports those rules as `incomplete`, which is not a violation, so the suite goes green and reads as a clean bill of health. It is also what makes "in both light and dark themes" mean anything at all: the two themes differ **only** in custom properties, which jsdom never resolves.

`unavailable-panel.a11y.spec.ts` is the smoke test for exactly that. It reads `--surface` off `documentElement` in each theme and asserts the value is non-empty and that the two differ. If that file passes, then Chromium launched, the application's real stylesheet was loaded through the target's `buildTarget`, `data-bs-theme` resolved the palette, and axe is reading genuine computed style. When another audit fails, check this one still passes before looking anywhere else.

### 3.1 The target

```jsonc
"test-a11y": {
  "builder": "@angular/build:unit-test",
  "options": {
    "buildTarget": "enterprise-gpt-ui:build:development",
    "runner": "vitest",
    "browsers": ["chromiumHeadless"],
    "browserViewport": "1280x900",
    "include": ["src/**/*.a11y.spec.ts"],
    "providersFile": "src/testing/test-providers.ts",
    "setupFiles": []
  }
}
```

Three of those lines are decisions:

- **`setupFiles: []`, deliberately empty.** `src/testing/setup-dom.ts` installs fakes for `matchMedia`, `IntersectionObserver` and `ResizeObserver`, because jsdom implements none of them. In Chromium those fakes would **shadow the real implementations** and answer `false` to every media query — so the responsive branches would never render and the audit would be of a layout no user sees.
- **`browserViewport: "1280x900"`.** Vitest's browser viewport defaults to 414×896. Without this line every audit in the suite ran at a phone width, where the sidebar, the data tables and the admin rail are not rendered at all — which is how the dark `--active-bg` failure had gone undetected (§6.2). This is the _desktop_ case; the responsive widths are driven per-test by `atViewport`.
- **The main `test` target excludes `src/**/*.a11y.spec.ts`.** They import `@vitest/browser/context`, which has no meaning in jsdom, and `axe-core`, which the unit suite has no use for.

The three devDependencies behind it are `@vitest/browser-playwright`, `playwright` and `axe-core`.

## 4. The harness

`src/testing/a11y.ts` exports five things.

**`expectNoSeriousViolations(root, label, options?)`** runs axe and fails with a readable report.

- **Only `serious` and `critical` fail.** `moderate` and `minor` are **discarded, not reported anywhere** — stated plainly here because the opposite reading would be a safety net that does not exist. `resultTypes: ['violations']` likewise means axe populates no `incomplete` bucket, so a rule it genuinely could not decide is silent. Both are deliberate: this is a gate, and a gate that prints advisories nobody reads trains people to skim its output.
- **A violation with no impact rating is reported rather than enforced.** axe types `impact` as nullable and optional; treating an unrated finding as critical would be the unsafe direction.
- **`region` is disabled by default.** The rule wants every node inside a landmark, and these specs mount one screen into a bare fixture host, so it would fire on the harness rather than on anything a user meets. The one audit that renders the real landmark structure — `shell.a11y.spec.ts` — passes `{ rules: {} }` and leaves it on. The option is **merged** into the default rather than spread over it, so a caller passing an unrelated rule cannot silently re-enable `region` for itself.

**`applyTheme(theme)` / `clearTheme()`** write `data-bs-theme` on `<html>` — the attribute, not `ThemeService`. What is under test is the rendered result, and a spec driving the service would still pass if the service stopped writing the attribute.

**`VIEWPORTS` and `atViewport(name)`** are the three widths US-1403 defines: `desktop` 1280×900, `tablet` 900×800, `mobile` 390×844.

**`expectNoHorizontalOverflow(root, label)`** is US-1403's last criterion, and only a real browser can answer it. Three details make it useful rather than noisy:

- It measures **descendants of the shell**, not `document.body`. Inside the signed-in app the shell is `height: 100dvh; overflow: hidden`, so the body can never scroll in either axis and a body-level assertion is vacuously true however badly a screen overflows.
- It reports a node only when `scrollWidth > clientWidth + 1` **and** its computed `overflow-x` is `hidden`. An `auto` or `scroll` container is a deliberate scroll region — a code block, the pill strip, a wide table — and reporting those would be reporting the design. Nodes 1px wide or narrower are skipped, because a `visually-hidden` element is a clipped 1×1 box whose content is _meant_ to overflow it; without that skip the check's first run produced three false positives and nothing real.
- **A pure text run that both ellipsises and refuses to wrap is contained, not reported.** `node.childElementCount === 0` plus a computed `text-overflow: ellipsis` and `white-space: nowrap` together mean the pattern is doing its job — pushing nothing sideways. `nowrap` is the load-bearing half of the pair: a `<td>` also sets `overflow: hidden` and `text-overflow: ellipsis`, but it *wraps*, so a cell clipping content it cannot actually ellipsise is still reported. The carve-out bounds containment only — it says nothing about whether the surviving text is still worth reading. Verified non-vacuous by reverting `PermissionBadge`'s own shrink fix, which fails `/admin/mcps` again at desktop and tablet, on `td.align-start`.

**Per-spec helpers build on top of these rather than reaching past them.** `admin.a11y.spec.ts`'s `openInShell(url, width)` calls `atViewport` and `open`, then narrows the returned element by the width the shell's own sidebar actually leaves at that breakpoint — 260px in the flow at desktop, a 60px strip at tablet, 0px once it is in the drawer at mobile. Auditing a routed screen against the bare viewport instead gives it room production never does: doing exactly that let two card-anatomy defects through a first pass of these audits, because the bare viewport had headroom the shell's own chrome always claims first (see [Administration §3.3.1](administration.md#331-six-defects-in-the-card-anatomy-and-four-bugs-found-while-fixing-them)).

## 5. What is covered

### 5.1 The routes

```ts
export const AXE_ROUTES = [
  '/chat',
  '/conversations',
  '/projects',
  '/documents',
  '/admin/users',
  '/admin/models',
  '/admin/mcps',
  '/admin/reports',
];
```

Every one of the eight is rendered in both themes. Eight spec files, **80 tests**:

| Spec                             | Tests | What it renders                                                                                                      |
| -------------------------------- | ----- | -------------------------------------------------------------------------------------------------------------------- |
| `shell.a11y.spec.ts`             | 14    | The whole frame at three widths × two themes, plus the same six checked for horizontal overflow, plus one landmark assertion |
| `admin.a11y.spec.ts`             | 26    | The four admin tabs × two themes, through `admin.routes.ts` with real stores and real data; plus nine viewport-fitting checks — the user directory, the model catalog and the MCP registry, each at desktop, tablet and mobile, through `openInShell` (§4) |
| `chat.a11y.spec.ts`              | 12    | The empty chat screen and an open conversation × two themes                                                          |
| `projects.a11y.spec.ts`          | 8     | The projects grid and the conversation library × two themes                                                          |
| `documents.a11y.spec.ts`         | 13    | The documents library × two themes × both of US-1003's view modes — the grouped default and `?view=flat` render different anatomies |
| `unavailable-panel.a11y.spec.ts` | 3     | The harness smoke test (§3), plus the panel itself × two themes                                                      |
| `activity-card.a11y.spec.ts`     | 2     | The tool-activity card × two themes                                                                                  |
| `email-card.a11y.spec.ts`        | 2     | The composed-email card × two themes                                                                                 |

The audits mount screens **through their real routes with real fixture data**, not components in isolation with empty state — which is the configuration in which almost nothing is wrong. Each attaches its element to `document.body`, because axe reads computed style and a detached element has none.

**`/documents` arrived with US-1002 (2026-08-19), exactly the way the guard demanded.** While EP-10 was unstarted the route was deliberately absent — under the rule the admin epic settled, a route is added by the story that builds it — and `a11y-coverage.spec.ts` asserted the absence _and_ its reason so US-1002 would meet a failing test rather than a silent gap. That story landed the route, its `AXE_ROUTES` entry and `documents.a11y.spec.ts` in one change and deleted the absence assertion with it, completing US-1405's four named route groups ([Documents Library §7](documents-library.md#7-accessibility)).

**`/ui-kit` is excluded deliberately.** It is `canMatch: [() => isDevMode()]` and never matched in a production build, so a violation there ships to nobody.

### 5.2 The coverage guard

`AXE_ROUTES` is hand-written, and the way such a list rots is a _new_ route: added to the router, never added here, audited by nothing, every gate green. `src/testing/a11y-coverage.spec.ts` closes that — it is the same shape of guard `check-tokens.mjs` applies to painted surfaces.

Two things about it are load-bearing:

- **It runs in jsdom, not in the browser target.** It reads the spec files off disk, which `node:fs` can do there and cannot do in Chromium. It asserts the coverage rather than performing it.
- **It requires the route to be an argument to `open(…)` or `navigateByUrl(…)`**, not merely to appear in the file. Matching raw text would be satisfied by a doc comment, a `provideRouter` table or a commented-out test — so deleting an `it` block while leaving the sentence above it would keep the guard green, which is precisely the rot it exists to catch. The boundary after the route matters too: without it `/chat` would be satisfied by `/chatter`, while `/chat/${conversation.id}` still counts, because both URLs are the same route through one `UrlMatcher`.

That separation is also why the route list lives in `a11y-routes.ts` rather than in `a11y.ts`: the latter imports `axe-core` at the top level, and this jsdom spec needs the list without a 600 kB auditor.

## 6. The two failures the run found

Both are colour, both were fixed **upstream in `docs/design/project/theme.css`** and mirrored into `_tokens.scss`, and both are now held there by new `CONTRAST_PAIRS` rows — **46 measured pairs, up from 38**. The fix had to go upstream because `check:tokens` enforces parity with the design bundle in the first place, so a local override would fail it; the PRD's accessibility-precedence rule is what authorises changing the board.

### 6.1 Light `--warn`, `#A96A10` to `#9C6210`

The open question in the PRD's §9, now closed. `#A96A10` measured 4.41:1 on white, 4.21:1 on `--bs-body-bg` and 3.99:1 on its own `--warn-bg` — all below AA's 4.5:1 for text under 18.66px — while being used as a `color` in eight stylesheets.

Three pairs now measure it, at `TEXT_MINIMUM` rather than the 3:1 non-text floor: some of the eight uses are icons, which would need only 3:1, but several are running text and one token serves both, so measuring the stricter of the two is the honest reading.

The third pair needed a new capability in the checker. `--warn-bg` is an opaque hex in light and an `rgba` in dark, so the dark half had been unmeasurable; a new **`under:`** option names the surface a translucent token is painted on, composites the two, and measures the result. One token that is translucent in one theme and opaque in the other is still one surface, and the reader sees whatever it resolves to.

The consequence for `_markdown.scss` is worth stating because the comment there changed: the diagram-failure notice still inherits `--bs-body-color` rather than `--warn`. The constraint that forced it is gone — the token now measures 4.56:1 on that panel — but the board draws it that way, `TurnNoticeCard` matches, and the gate now measures the pair either way.

### 6.2 Dark `--active-bg`, `#173A58` to `#16354D`

**This one the axe run found, not the table** — and only once the browser viewport was pinned to a desktop width, because the surfaces that use it are not rendered on a phone. `--brand` on `--active-bg` measured **4.29:1** in dark against a 4.5:1 minimum for 13.5px text, and it is the pairing behind three live controls: the administration rail's active tab, the sidebar's active navigation link, and the paginator's current page.

It had been invisible for three stories, and the reason is instructive. US-1401's surfaces guard checks every painted background against `--focus-ring` — that is, against the _ring_ drawn on it, not against the _text_ drawn on it. Nothing measured the text, so nothing failed. The new row does, and makes the fix permanent.

## 7. Budgets

Both budget lines are re-baselined here, because US-1401 explicitly left that to this story.

|                    | Measured      | Warn        | Error |
| ------------------ | ------------- | ----------- | ----- |
| Initial raw        | **670.33 kB** | 670 → **675 kB** | 720 kB (unchanged) |
| `styles` bundle    | **64.47 kB**  | 65 → **66 kB**   | 80 kB (unchanged)  |

The error ceilings are the numbers that matter and neither moved. The warning lines moved because the previous baseline left 0.07 kB of headroom, which is not headroom — it is a build that fails on the next comment.

`check-initial-chunk.mjs` gained an `axe-core` entry alongside the diagram and math libraries. `axe-core` is a devDependency of roughly 600 kB reached only from `*.a11y.spec.ts` through `src/testing/a11y.ts`; an accidental application import would not merely regress the budget, it would ship an accessibility auditor to every user. Nothing else would catch it, because the budgets measure the build and a spec-only dependency is invisible to them.

## 8. CI

Two steps were added to the existing `Lint, test, build` job in `ui-ci.yml`, plus a failure-only artifact upload. **Job names are unchanged, so branch protection needs no change** — which is the reason it is a step and not a job: a new required check has to be added by hand in a repository setting, and a required check nobody adds is a gate that never runs.

```yaml
- name: Install Chromium for the accessibility audit
  run: npx --no playwright install --with-deps chromium
- name: Accessibility
  run: npm run test:a11y
```

- **Last, and after `Build`.** This is the most expensive step in the job — it runs a second Angular build of its own and launches a browser — so ahead of `Build` one contrast failure would hide the bundle-budget result and cost a second round trip. The job's ordering rule is unchanged: the cheapest gates report first.
- **`--with-deps`** installs the system libraries Chromium needs on a bare runner.
- **`--no`** makes a missing local binary a loud failure instead of npx silently fetching an unpinned `playwright` from the registry and running it against the checkout. The version is pinned by `package-lock.json` and by nothing else, since dependabot tracks only the `github-actions` ecosystem here.
- **The screenshot upload is `if: failure()` with `if-no-files-found: ignore`** — the opposite of the JUnit upload beside it. A green run leaves nothing behind by design, so an empty path is the normal case rather than a sign the reporter broke. Vitest's browser mode writes a screenshot per failed assertion, which is the only evidence a violation reproducing solely on CI leaves anywhere. Both paths carry the `enterprise-gpt-ui/` prefix, because `upload-artifact` resolves against the workspace root and ignores `defaults.run.working-directory`.

`__screenshots__/` and `.vitest-attachments/` are gitignored: they are diagnostics, and the audit is reproducible from the spec.

## 9. Limits, recorded rather than hidden

- **`moderate` and `minor` violations are discarded**, and `incomplete` results are never populated (§4). Nothing surfaces them.
- **Automated testing finds a minority of accessibility defects.** This run is a floor, not a certificate; US-1402's screen-reader pass against real assistive technology is still outstanding and is tracked in the build order.
- **The audits mount routes, not the full application.** Only `shell.a11y.spec.ts` renders the real landmark structure, which is why it is the only one with `region` enabled.
- **The suppressed-indicator scan still reads `src/` only** — a US-1401 limit this story did not close; axe now measures the rendered result, which narrows the gap without shutting it.

## 10. Troubleshooting

| Symptom                                                                                | Cause and fix                                                                                                                                                              |
| -------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `npm run test:a11y` fails immediately with a browser launch error                       | Chromium is not installed. `npx --no playwright install chromium` (§2)                                                                                                     |
| Every audit passes but the smoke test fails on `--surface`                              | The stylesheet did not reach the browser. Check `buildTarget` is still on the target, and that `setupFiles` is still `[]` (§3.1)                                            |
| A responsive branch never renders and the audit reports nothing                          | `setup-dom.ts` came back into `setupFiles`, so its `matchMedia` fake answers `false` to every query (§3.1)                                                                  |
| The sidebar, tables or rail are missing from an audit                                    | `browserViewport` was dropped and the run fell back to 414×896 — the failure mode that hid §6.2 for three stories                                                           |
| `axe coverage (US-1405)` fails naming a route                                            | The route is in `AXE_ROUTES` and no audit navigates to it. Add the audit, or remove the entry — a doc comment will not satisfy the guard (§5.2)                             |
| `check:tokens` fails on `--warn` or `--active-bg`                                        | A token was changed in `_tokens.scss` without the design bundle, or vice versa. Both corrections in §6 live upstream in `theme.css`                                         |
| `check:tokens` reports a background as unmeasurable in one theme only                    | It is translucent in that theme. Give the pair an `under:` naming the surface beneath it (§6.1)                                                                             |
| `npm run build` fails naming "the axe auditor"                                           | Application code imported `axe-core`, or imported `src/testing/a11y.ts`. Only `*.a11y.spec.ts` may (§7)                                                                     |
| The build warns on the initial bundle after a small change                                | The warn line is 675 kB against a 670.33 kB baseline. Check first whether the new dependency has to be initial at all (§7)                                                  |
| A violation reproduces on CI and not locally                                             | Download the `ui-a11y-failures` artifact; the browser-mode screenshots are the only record (§8)                                                                             |

## 11. Key files

| Concern                | File                                                                                                                                                                                 |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| The target and script  | [`angular.json`](../../enterprise-gpt-ui/angular.json), [`package.json`](../../enterprise-gpt-ui/package.json)                                                                        |
| The harness            | [`src/testing/a11y.ts`](../../enterprise-gpt-ui/src/testing/a11y.ts)                                                                                                                  |
| The route record       | [`src/testing/a11y-routes.ts`](../../enterprise-gpt-ui/src/testing/a11y-routes.ts)                                                                                                    |
| The coverage guard     | [`src/testing/a11y-coverage.spec.ts`](../../enterprise-gpt-ui/src/testing/a11y-coverage.spec.ts)                                                                                      |
| Contrast pairs         | [`scripts/check-tokens.mjs`](../../enterprise-gpt-ui/scripts/check-tokens.mjs)                                                                                                        |
| Initial-graph guard    | [`scripts/check-initial-chunk.mjs`](../../enterprise-gpt-ui/scripts/check-initial-chunk.mjs)                                                                                          |
| CI                     | [`.github/workflows/ui-ci.yml`](../../.github/workflows/ui-ci.yml)                                                                                                                    |
| Related reference      | [Design System](design-system.md), [Responsive layout](responsive-layout.md), [Answer Rendering](answer-rendering.md), [Pull-request checks](../ci/pull-request-checks.md)            |
