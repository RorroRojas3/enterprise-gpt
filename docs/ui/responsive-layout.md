# Responsive layout

How the Angular client at `enterprise-gpt-ui/` behaves below a desktop width: the one place the application's breakpoints are stated, the three layout modes the shell switches between, the navbar seam a routed screen publishes into, and what every other screen does when the viewport gets narrow.

Audience: a developer adding a screen, a stylesheet or an overlay panel that has to survive a phone — and anyone about to write a media query, who should read §3 first, because `npm run lint` will refuse a hand-written breakpoint.

Companion to [Shell and Navigation](shell-and-navigation.md), which owns the shell's own policy in §5.5, and to [Design System](design-system.md) for the token layer these rules read through. [The rebuild PRD](../prd/enterprise-ui-rebuild.md) is the authority for every `US-xxx` reference; bare `§` references below are to sections of _this_ page.

## 1. Overview

US-1403 is a **conformance** story, not a build one. Most of what its criteria ask for was already true, because `DataTable` collapses to cards, `PillSubnav` exists, and the sidebar already had two widths. Reading the criteria against the working tree gives this:

| Criterion                                                                                                  | Status when the story opened                                                                                                                       |
| ---------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1 — persistent sidebar at ≥ 1024px, 820px conversation column                                              | Already held (US-301, US-401)                                                                                                                      |
| 2 — 768–1023px: strip by default, expanding as an overlay (frame `3g`)                                     | **Built here**                                                                                                                                     |
| 3 — below 768px: navbar hamburger, full-screen sidebar, full-width composer and pickers (frames `1d`, `3e`) | **Built here**                                                                                                                                     |
| 4a — admin tables become stacked cards at 44px targets                                                     | Already held (US-106's `DataTable`, US-1201)                                                                                                       |
| 4b — the admin sub-nav becomes scrollable pills (frame `5m`)                                               | Already held (US-1201's `PillSubnav`)                                                                                                              |
| 4c — the project-detail tab strip becomes stacked accordions                                               | **Departed from deliberately** — it becomes the same pill strip (§8.5)                                                                             |
| 5 — the reports charts stack into one column below 1024px                                                  | **Vacuous when this story opened** — `/admin/reports` was an `UnavailablePanel`, and there were no charts to stack. **Retired by US-1302, 2026-08-21** (§10) |
| 6 — no horizontal scrollbar at any of the three widths                                                     | **Gated here**, in a real browser — jsdom computes no layout and cannot answer it ([Accessibility audit §4](accessibility-audit.md#4-the-harness)) |

So the work divides into five things, and only the second is a screen:

1. **One breakpoint record** replacing six spellings of two numbers across twelve stylesheets, with two build gates keeping it honest (§3).
2. **The shell's three modes**, and the fact that the same chevron now means two different things (§4).
3. **A navbar seam**, `ShellChrome`, so a routed screen can put its own title and controls into a bar the shell owns (§5).
4. **Overlay panels that stay on screen** — which fixed a defect present at _every_ width, not only on a phone (§6).
5. **Safe-area insets**, which were a no-op until `index.html` opted in (§7).

Two pre-existing bugs fell out of the work and are recorded in §9: a projects grid written against Bootstrap classes this application does not ship, and four routed screens that were being clipped below the fold at every width.

### 1.1 Where each piece lives

| Concern                                 | Where                                                                                                                                                                                                                                     |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The breakpoints, for stylesheets        | [`src/styles/_breakpoints.scss`](../../enterprise-gpt-ui/src/styles/_breakpoints.scss) — variables and five mixins                                                                                                                        |
| The breakpoints, for TypeScript         | [`shared/layout/breakpoints.ts`](../../enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts)                                                                                                                                            |
| A media query as a signal               | [`shared/layout/media-query.ts`](../../enterprise-gpt-ui/src/app/shared/layout/media-query.ts) — `injectMediaQuery`                                                                                                                       |
| The three modes, and all sidebar policy | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts), [`shell.html`](../../enterprise-gpt-ui/src/app/features/shell/shell.html), [`shell.scss`](../../enterprise-gpt-ui/src/app/features/shell/shell.scss) |
| The controlled sidebar                  | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts) — `mode`, `toggled`, `searchRequested`                                                                                           |
| Frame `1d`'s bar                        | [`features/shell/mobile-navbar/`](../../enterprise-gpt-ui/src/app/features/shell/mobile-navbar/)                                                                                                                                          |
| The navbar slot a screen publishes into | [`core/ui/shell-chrome.ts`](../../enterprise-gpt-ui/src/app/core/ui/shell-chrome.ts)                                                                                                                                                      |
| Overlay placement                       | [`shared/overlay/anchored-panel.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/anchored-panel.ts) — `anchoredPosition`                                                                                                               |
| The drawer, and its `bare` mode         | [`shared/overlay/offcanvas/`](../../enterprise-gpt-ui/src/app/shared/overlay/offcanvas/)                                                                                                                                                  |
| Safe-area tokens                        | [`src/styles/_kit.scss`](../../enterprise-gpt-ui/src/styles/_kit.scss), [`src/index.html`](../../enterprise-gpt-ui/src/index.html)                                                                                                        |
| The two gates                           | [`scripts/check-forbidden-apis.mjs`](../../enterprise-gpt-ui/scripts/check-forbidden-apis.mjs), [`scripts/check-tokens.mjs`](../../enterprise-gpt-ui/scripts/check-tokens.mjs)                                                            |

## 2. Quick start

### 2.1 Making a stylesheet respond

```scss
@use 'breakpoints' as bp;

@include bp.mobile {
  /* below 768px */
}
@include bp.tablet {
  /* 768–1023px */
}
@include bp.desktop {
  /* 1024px and up */
}
@include bp.below-desktop {
  /* everything under 1024px */
}
@include bp.tablet-up {
  /* 768px and up */
}
```

`@use 'breakpoints'` resolves by bare name from anywhere under `src/`, because `angular.json`'s `stylePreprocessorOptions.includePaths` names `src/styles` and applies to component stylesheets as well as to the global chain.

**This is the one partial a component stylesheet may `@use`.** `_tokens.scss` may not, and the difference is not taste: that file emits `:root` blocks, so every component loading it would re-emit the whole token table into its own scoped stylesheet. `_breakpoints.scss` emits **nothing** until a mixin is included, so it costs zero bytes however many stylesheets load it — which is also why a top-level declaration must never be added to it.

### 2.2 Reading the mode in TypeScript

```ts
import { MOBILE_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';

protected readonly isNarrow = injectMediaQuery(MOBILE_VIEWPORT);
```

`injectMediaQuery` writes a signal from the `change` listener, which is the zoneless-correct way to notify Angular of something that happened outside its own event handling. It must be called in an injection context, and it reads `matches` once at construction — so a cold load on a phone renders the phone frame rather than waiting for a resize.

### 2.3 Putting a control in the mobile navbar

```ts
const chrome = inject(ShellChrome, { optional: true });
if (chrome !== null) {
  chrome.publish({ title: 'Admin' });
  inject(DestroyRef).onDestroy(() => chrome.clear());
}
```

Always `{ optional: true }`, and always cleared on destroy. §5 explains both.

### 2.4 Rules that are not style preferences

- **Never write `767px`, `767.98px`, `768px`, `1023.98px` or `1024px` into a media query.** `npm run lint` fails on it, naming the mixin to use instead (§3.3).
- **Never change one copy of a breakpoint without the other**, and never pair a whole-number maximum with a whole-number minimum (§3.2).
- **A `position: fixed` panel is sized from `documentElement.clientWidth`, never `window.innerWidth`** (§6.2).
- **A surface that touches a screen edge adds the safe-area inset back** (§7).
- **A routed screen inside the shell owns its own scrolling** (§9.2).

## 3. One breakpoint record, two copies, two gates

### 3.1 Why the record exists

The design boards restructure at two widths and only two: 768px, where tables become cards and the sidebar leaves the flow, and 1024px, where the sidebar stops being persistent. The application agreed with that in intent and disagreed with itself in spelling — twelve stylesheets and two components carried **six different forms** of those two numbers, including `767px` in some files and `767.98px` in others.

They are not the same edge. `(max-width: 767px)` beside `(min-width: 768px)` leaves a **dead zone**: a viewport reported as 767.5px — browser zoom, or the Windows display scaling this application ships on — matches _neither_ query. Under US-1403 that would mean a user with no sidebar in the flow and no navbar either.

So the two edges are stated once per language, because Sass cannot read a TypeScript constant and `matchMedia` cannot read a Sass variable:

| Mode        | SCSS                                | TypeScript                                                          |
| ----------- | ----------------------------------- | ------------------------------------------------------------------- |
| Mobile      | `bp.mobile`                         | `MOBILE_VIEWPORT` — `(max-width: 767.98px)`                         |
| Tablet      | `bp.tablet`                         | `TABLET_VIEWPORT` — `(min-width: 768px) and (max-width: 1023.98px)` |
| Desktop     | `bp.desktop`                        | (both queries false)                                                |
| Under 1024  | `bp.below-desktop`                  | —                                                                   |
| 768 and up  | `bp.tablet-up`                      | —                                                                   |
| As numbers  | `$bp-tablet-min`, `$bp-desktop-min` | `TABLET_MIN_WIDTH`, `DESKTOP_MIN_WIDTH`                             |

**The two queries are mutually exclusive on purpose, and desktop is the fall-through.** A `mode` derived from them must read `desktop` when both are false, because `src/testing/media-query.ts`'s fake answers `false` for every query a spec has not set — so "nothing configured" has to mean the mode the existing suite was written against. That fake now **re-exports** `MOBILE_VIEWPORT` rather than restating it, since it matches on the query _string_, and a spec driving a value the code never asks for silently drives nothing.

Mixins rather than bare variables at the call sites, because `@media (max-width: $bp-tablet-min - 0.02px)` written twelve times is twelve chances to write `- 1px` instead.

**What is deliberately not in the record**: `project-files.scss` at 575px, the two admin form dialogs at 480px, the auth interstitial at 575.98px. Those are components reflowing their own contents, not the application changing mode, and folding them in would make the record mean less rather than more.

### 3.2 The parity gate

`check-tokens.mjs` compares the two copies on every `npm run lint`, in both halves:

- the whole-number minima — `$bp-tablet-min` against `TABLET_MIN_WIDTH`, and the desktop pair;
- **every maximum in either file must be exactly 0.02px below a declared minimum.** That is the half that rots silently: a `767px` maximum looks tidier and reintroduces §3.1's dead zone. The comparison rounds rather than comparing raw floats, because `767.98 + 0.02` landing on 768 at double precision is luck the next pair of numbers may not have.

Both files are **comment-stripped before scanning**, because both document the bug by quoting the wrong queries; scanning the prose would fail the gate on its own explanation.

### 3.3 The forbidden-pattern gate

`check-forbidden-apis.mjs` gained a rule refusing **a hardcoded application breakpoint** — any `(max-width:` or `(min-width:` naming 767, 767.98, 768, 1023.98 or 1024. Only `_breakpoints.scss` and `breakpoints.ts` are exempt, and the failure message names the mixins and the constants, so the fix is in the failure rather than in this page.

It is a text scan for the reason the other eight rules are: it sees a value assembled at run time, it sees an inline `eslint-disable`, and it runs identically on a Windows developer's machine and on CI.

## 4. The three modes

The shell reads the two queries and derives one `mode`. What each mode renders:

| Mode      | Width      | Frame       | Sidebar                                                                                      | Chrome above the screen |
| --------- | ---------- | ----------- | -------------------------------------------------------------------------------------------- | ----------------------- |
| `desktop` | ≥ 1024px   | `3f` / `3b` | In the flow, 260px or the 60px strip — **persisted**                                          | none                    |
| `tablet`  | 768–1023px | `3g`        | 60px strip by default; expands as a floating 260px panel over a backdrop — **not persisted** | none                    |
| `mobile`  | < 768px    | `1d` / `3e` | Not in the flow at all; lives inside a modal drawer                                           | the 54px navbar         |

### 4.1 Tablet: why the main content provably does not reflow

Criterion 2 says the main content "never reflows" when the panel opens. That could be satisfied by arithmetic — animate a width, subtract it somewhere — and it is instead satisfied **structurally**. When the overlay is open the sidebar leaves the flow (`position: absolute`, 260px, with a shadow) and a `.shell__rail` spacer holds the 60px track it vacated. The flex row `.shell__main` is laid out against is byte-for-byte the same open or closed, so there is no arithmetic left to get wrong.

`.shell__main` takes `inert` while the panel is up. The backdrop already makes the content unclickable; without `inert` it would still be reachable by Tab, which is a backdrop that lies to the keyboard. The mobile drawer needs none of this, because a modal `<dialog>` inerts the page itself.

Dismissal is `pointerdown` on the backdrop, Escape on the shell host, or the chevron. Two of the three **return focus to the strip control that replaced the one that was pressed** — otherwise the user is left on `<body>`, where the next Tab restarts at the top of the page.

One consequence reaches the skip link, which is a sibling of `<main>` rather than a descendant and so is _not_ inerted with it: while the overlay is up, following it would try to focus an inert element and do nothing at all, silently. It therefore dismisses the overlay first and focuses `<main>` on the render that removes the attribute — writing the signal only schedules change detection, so focusing in the same task would hit the still-inert element.

### 4.2 Mobile: the drawer is the shared `Offcanvas`

Frame `3e` is rendered by the existing `Offcanvas` — a native `<dialog>` opened with `showModal()`, which supplies the top layer, real inerting, `aria-modal` and a cancellable Escape — rather than by a second overlay wrapper. Sharing the mechanism keeps one set of focus and dismissal semantics instead of two, which is the rule that component's own documentation states.

It gained one input, `bare`, for content that already owns its edges: it zeroes the panel's padding and gap, makes the body a containing block so a consumer can put a close button in the corner, and stops the body scrolling because the sidebar inside scrolls its own list.

**`bare` clips the panel's `<h2>` rather than hiding it.** `aria-labelledby` points at that heading, so `display: none` would take it out of the accessibility tree and leave a modal dialog unnamed. It is the same call, for the same reason, that `conversations.scss` makes on its order explanation and that `chat.scss` now makes on the chat `<h1>` (§5.1).

The drawer's close control belongs to the **shell**, not the sidebar, because what it dismisses is the shell's overlay — which is also why the sidebar suppresses its own collapse chevron in that mode. Two dismissals that mean different things is worse than one.

### 4.3 The rest of the policy lives on the shell

`Sidebar` became a controlled component in this story — `mode` in, `toggled` and `searchRequested` out, and no `UiStore` injection — because the same chevron persists a width at desktop and opens a transient overlay at tablet, and only the shell can see which. The persisted `egpt.sidebar` preference is read **and written at desktop only**, the overlay flag is a `linkedSignal` on the shell rather than a field on `UiStore`, and exactly one `<app-sidebar>` declaration is rendered at one of two outlets. Each of those is a decision with a failure mode behind it, and they are written up where a reader of the shell will meet them: [Shell and Navigation §5.5](shell-and-navigation.md#55-three-viewport-modes-us-1403).

## 5. The mobile navbar, and the `ShellChrome` seam

Frame `1d` draws **one** 54px bar below 768px: a hamburger, the brand mark, a title, and trailing controls. The hamburger belongs to the shell, because the sidebar does. The title and the trailing controls belong to the **routed screen** — chat puts its favourite star and conversation kebab there, administration puts the word "Admin" there and nothing else.

That is a seam, and it is the same seam `COMPOSER_HOST` is: the two halves sit in `features/shell` and `features/chat`, dependencies run one way, and neither feature may import the other — so what they share has to sit below both, in `core/`.

```ts
export interface ShellChromeState {
  readonly title: string | null;
  readonly actions: TemplateRef<unknown> | null;
}
```

Four things about it are decisions rather than details:

- **A `TemplateRef`, not a descriptor array.** The one real consumer is the chat header's kebab: a `Menu` with a conditional item, a danger item, an `aria-haspopup="dialog"` item and an anchored project picker. Encoding that as data would mean the shell rebuilding the header's menu from a second description, and the two drifting.
- **`@Injectable()` with no `providedIn`, provided at the `Shell` component.** Nothing eagerly reachable imports the file, so it rides the lazy shell chunk. `providedIn: 'root'` would put it on an initial graph with well under a kilobyte of headroom, to hold state only a lazy component renders — which is also why it must not be listed in `app.routes.ts`, which _is_ on that graph.
- **Injected `{ optional: true }`.** Every route spec renders these screens through `RouterTestingHarness` with no shell around them, and a required dependency would make this seam a reason to rewrite them all.
- **A publisher must `clear()` on destroy**, or the chat kebab outlives the chat route and appears over the projects screen. `clear()` is idempotent.

The navbar's own markup carries two accessibility calls worth keeping. The hamburger is `aria-haspopup="dialog"` with **no** `aria-expanded`, because what opens is a modal `<dialog>` in the top layer that inerts the button rather than revealing a region beside it — the same call `chat.html` makes on "Move to project". And the title is a `<span>`, never a heading: the routed screen below owns the document's only `<h1>`, and promoting brand text to a heading would give the page two.

### 5.1 The chat header gives up its bar, not its heading

Below 768px `.chat__header` collapses to zero height and the star and kebab render in the navbar instead. That retires the last of the three criteria deferred out of US-308 back in EP-3.

The `<h1>` stays, **clipped rather than removed**. It is the document's only level-one heading, and `display: none` would take it — and the skeleton that stands in for a name still in flight — out of the accessibility tree with it. jsdom applies no stylesheet, so what `chat.spec.ts` can assert is that the element and its text are still in the DOM at that width; the rendered result is checked by the axe run.

The star and kebab are **one `<ng-template>` rendered at exactly one place**. A `TemplateRef` instantiated in both the header and the navbar would put two of that menu — and two project pickers, each with its own open state — in the document at once, which `chat.spec.ts` pins from both directions.

## 6. Overlay panels

### 6.1 A clamp that fixes a defect at every width

`anchoredPosition` now takes the viewport's `{ width, height }` and returns a `width` alongside `top`/`bottom`/`left`. Two behaviours came with that, and only one of them is about phones:

- **`left` is clamped into the viewport.** Nothing clamped it before, so a 360px model picker opened `align="start"` from a pill near the right edge simply ran off screen — on a desktop browser as readily as on a tablet. A pre-existing defect that US-1403 found rather than caused.
- **Below 768px, or whenever the panel cannot fit beside its 12px gutters, the panel spans the viewport instead.** Clamping alone is not enough there: a 360px panel does not fit a 390px viewport with anything left over, and a clamped panel no longer visually belongs to the control that opened it.

Neither branch touches `up`/`down`. Upward panels still anchor by `bottom`, so a panel whose content swaps while open grows away from its trigger rather than over it.

### 6.2 `documentElement.clientWidth`, never `innerWidth`

Both callers — `Menu` and `ProjectPicker` — now read the viewport in the **measure** phase beside the anchor rect, because both are layout reads and the phase split exists to keep them out of the write pass. The width comes from `document.documentElement.clientWidth`.

That is not interchangeable with `window.innerWidth`. A `position: fixed` panel's containing block **excludes** the classic scrollbar, so sizing from `innerWidth` overhangs by the scrollbar's thickness and produces exactly the horizontal scrollbar criterion 6 forbids — on the desktop browser someone narrows in order to check the phone layout. It is the JavaScript spelling of the `100vw` mistake `offcanvas.scss` calls out one file over, where `max-width: 100vw` became `max-width: 100%` for the same reason.

### 6.3 The width is the intended one, not the measured one

Both callers pass their **intended** panel width — `Menu`'s `panelWidth()` input, `ProjectPicker`'s `PANEL_WIDTH` constant — rather than `offsetWidth`. The measurement reflects whatever width was bound on the previous pass, so the first open at a narrow width would compute its inset from a stale number.

`ProjectPicker`'s 320px moved out of the stylesheet into the component for the same reason: the placement decides whether the panel still fits, and the number cannot live in two files where one of them decides and the other renders. The width is written inline at every viewport, as `Menu` already did.

## 7. Safe areas

```html
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
```

`viewport-fit=cover` is not cosmetic. Without it **every `env(safe-area-inset-*)` resolves to 0px**, so the safe-area padding frame `1d` asks for would be a no-op that looks implemented. With it the layout viewport extends _under_ the notch and the home indicator, which is why every surface that touches an edge now has to add the inset back.

`_kit.scss` exposes the four insets as tokens — `--safe-top`, `--safe-bottom`, `--safe-left`, `--safe-right` — for the reason `--toast-top` is a token: the fallback belongs in one place, and `env()` with no fallback is invalid in a browser that does not know the keyword, which fails as one dropped declaration rather than visibly.

Five consumers: the mobile navbar, the chat composer, the transcript gutters, the drawer, and the toast region — whose `--toast-top` becomes `calc(78px + var(--safe-top))` below 768px, because the bar above it there is the 54px navbar rather than the 52px chat header.

**Removing the meta tag silently un-pads all five; removing their padding silently puts content under the notch.** Neither shows up in a test, which is why both halves are commented at their sites.

## 8. What each screen does

### 8.1 Administration (already held)

The 190px rail becomes frame `5m`'s scrollable pill strip below 768px, and `DataTable` renders `CardRow`s instead of a grid at the same width. Both predate this story; what changed is that they read the breakpoint from the record now instead of spelling `(max-width: 767px)` themselves — which also closed a real 0.98px gap between the rail's old whole-number query and the record's fractional one. See [Administration §3.3](administration.md#33-below-768px).

### 8.2 The conversation library and the projects grid

Both already dropped their 1440px gutters to `18px 14px` below the breakpoint. Both now scroll themselves (§9.2), and the projects grid is a real grid for the first time (§9.1).

### 8.3 Project detail

The three-tab strip becomes `PillSubnav` below 768px, with the same links, the same routes and the same `aria-current="page"`. §8.5 explains why that is not frame `4e`'s accordion.

### 8.4 Chat

The 52px header collapses, its controls move into the navbar (§5.1), and the transcript and composer take frame `1d`'s gutters — 16px and 12px respectively, each with the matching safe-area inset **added rather than substituted**, because `viewport-fit=cover` puts both edges under the device's rounded corners in landscape.

#### 8.4.1 The composer's compact control row

A follow-on fix, closing a gap this story's own criterion 6 (no horizontal scrollbar at any width) left in the composer specifically. `.composer__controls` is a single non-wrapping flex row — the project pill, the model pill, the tools pill, the microphone and Send — and only the project pill had ever been let shrink: the model and tools pills are `:host { display: inline-flex }` with `white-space: nowrap` and no `min-width: 0` anywhere in their chain, so each sat at its intrinsic text width. At 390px the row asked for roughly 625px against roughly 342px of card content, and `composer.scss` carried no `@media` and no `@use 'breakpoints'` at all — the gutters in §8.4 above never reached inside the composer's own row. The overflow ran past the card, and `Shell`'s `:host { overflow: hidden }` (§9.2's rule, applied to itself) clipped whatever fell off the end, which was Send.

The fix follows the design bundle's `compact` variant of `Composer.dc.html`:

- **the download control is hidden outright**, `app-conversation-download-menu { display: none }` — the mobile navbar (§5) already renders the same export menu through `ShellChrome`, so the action stays one press away rather than gone
- **the project pill drops its label**, leaving the icon and caret. On the project screen the static `.composer__project--fixed` chip — a plain `<span>` whose only content is that label — is hidden **entirely** below 768px rather than losing just its text: a pill with an icon and nothing to read announces nothing to a screen reader, and that screen's own header already names the project
- **the model pill drops its context figure** (`400k`)
- **the tools pill drops its label only while nothing is armed.** `toolsLabel()` is a count — `Tools`, `1 Tool`, `2 Tools` — so it is the row's only visible sign that MCP servers will be sent on the next turn. A `.tools-menu__pill--armed` class, set from `settings.effectiveMcpServerIds().length > 0`, keeps the label whenever that count is non-zero
- **the model name is made to actually shrink and ellipsis.** That needed `min-width: 0` at four levels, each owned by a different stylesheet, because the shrink has to travel down through a projected trigger: `model-menu.scss`'s `:host`, the `app-menu` host it wraps (granted from `model-menu.scss` rather than from `menu.scss` itself, since most menu triggers are fixed 28px kebabs that must never shrink), `menu.scss`'s `.menu__trigger--content`, and `.model-menu__pill`. The project pill and the model pill are the row's only two elastic items, and with the project name hidden below 768px, all of the shrink lands on the model name — unconditionally, because the name comes from the catalog and no width is wide enough for every one of them

Measured with a Playwright harness driven against the compiled CSS, at nine widths from 320px to 430px plus 768/900/1100/1400px: before the fix, Send's right edge sat 105px past the card's own right edge at 390px, and the document body scrolled sideways at every width under 768px. After, Send is visible at every measured width, the row's `scrollWidth` equals its `clientWidth`, and the model name ellipsises below roughly 393px — a 39-character name truncates from 232px down to 22px at 320px and still leaves the tools count showing beside it.

### 8.5 The one departure from the boards

Frame `4e`'s caption asks for the project-detail tabs to become **stacked accordions** below 768px. They become the pill strip instead. This is a deliberate departure, agreed with the product owner before implementation, and the reason is not layout:

The three tabs are **real child routes**, one of them behind a `canDeactivate` guard that can _refuse_ the navigation. A disclosure control marked `aria-expanded` would announce a state the control neither owns nor can guarantee — the user presses it, the guard opens a modal, the navigation is cancelled, and the control has already said it is expanded. `aria-current="page"` on a link is the honest statement of the same thing, and reusing `PillSubnav` leaves the application with one responsive navigation pattern rather than two.

`PillItem` gained an optional `count` for it, rendered as the board's smaller run beside the label and **withheld while `null`** rather than shown as zero — "Files 0" on a project that has four is worse than no count at all, which is the rule the wide strip already followed.

## 9. Two pre-existing bugs

### 9.1 A projects grid built from classes that do not exist

The projects screen asked for its three-up card grid with `row g-3` and `col-12 col-md-6 col-xl-4`. **None of those classes is defined.** `_bootstrap-components.scss` deliberately omits `bootstrap/scss/grid` at 10.8 kB, with a comment promising an intrinsic replacement — so the "grid" had been a plain block stack at every width since the screen shipped, on a laptop as much as on a phone.

It is now `repeat(auto-fill, minmax(min(280px, 100%), 1fr))` with a 16px gap. `min(280px, 100%)` rather than the bare 280px that comment named: a hard 280px track minimum overflows its container below roughly a 308px viewport, which is precisely the horizontal scrollbar criterion 6 forbids.

### 9.2 Four routed screens clipped below the fold

`.shell__main` is `overflow: hidden`, which is what keeps the shell a fixed-height frame. A routed screen that does not scroll itself is therefore **clipped**, not scrolled — silently, and at every width. Only the chat route escaped it, because `.chat__body` is its own scroll container.

Conversations, projects, project detail and the admin layout each gained `overflow-y: auto` on their host. Three of them also need `min-height: 0`, for the same reason `shell.scss` needs `min-width: 0`: a flex item will not shrink below its content, so `overflow-y` alone would never engage. `AdminLayout` is the exception and keeps its `min-height: 100%`, which is what makes the rail span the pane when a tab's content is short; the pane has a definite height, so the percentage resolves and the host scrolls its overflow instead of shrinking to avoid it.

## 10. Limits, recorded rather than hidden

- **Criterion 6 is gated in the browser only.** `shell.a11y.spec.ts` checks it at three widths in both themes; jsdom cannot answer it at all, so the jsdom suite pins the structure instead.
- **The overflow check reports only containers that clip.** `overflow-x: auto` or `scroll` is a deliberate scroll region — a code block, the pill strip, a wide table — and reporting those would be reporting the design. Elements 1px wide or narrower are skipped too, because a `visually-hidden` box is a clipped 1×1 whose content is _meant_ to overflow it; without that skip the check's first run reported three of them and nothing real.
- **The project-detail pills are a departure the boards do not describe** (§8.5), recorded in [the build order](../prd/enterprise-ui-rebuild-build-order.md)'s interim-behaviours table so it is not later mistaken for drift.
- **Nothing here has been exercised on a real phone or tablet** (§11).

## 11. Testing

| Spec                          | Count | Notable cases                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| ----------------------------- | ----- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `shell-responsive.spec.ts`    | 10    | The strip defaulting at tablet **whatever the stored preference says**; the overlay expanding without ever writing that preference; `inert` on the main content while it is up; a backdrop press collapsing it and handing focus back to the strip; the navbar rendering with no sidebar in the document until the drawer opens; opening from the hamburger, focusing the close control and returning focus to it; the sidebar's own chevron suppressed inside the drawer; the phone frame on a **cold load** rather than only after a resize; the overlay dismissed when the viewport crosses a breakpoint |
| `anchored-panel.spec.ts`      | 12    | Both vertical anchors unchanged by the new branch; start and end alignment where the panel fits; clamping at both edges; the full-width branch below 768px for both alignments; **768px still anchoring**, as the first width that is not mobile; a panel too wide for its gutters spanning even above the breakpoint                                                                                                                                                                                                                                                     |
| `mobile-navbar.spec.ts`       | 4     | The product name until a screen publishes one; published controls rendered, and none when none is published; the hamburger named and saying what it opens; the brand text kept out of the heading outline                                                                                                                                                                                                                                                                                                                                                                |
| `chat.spec.ts` (new describe) | 4     | Exactly **one** instantiation of the header menu at each width; the header giving up its controls below 768px while the slot still holds them; the `<h1>` present at every width; the slot cleared on destroy                                                                                                                                                                                                                                                                                                                                                            |
| `shell.a11y.spec.ts`          | 13    | axe at three widths × two themes with `region` enabled; no horizontal overflow at the same six; one navigation landmark whatever the width                                                                                                                                                                                                                                                                                                                                                                                                                               |

```bash
# from enterprise-gpt-ui/
npm test            # Vitest in jsdom — 1750 specs
npm run test:a11y   # Vitest in Chromium — 32 audits, including criterion 6
npm run lint        # includes the breakpoint gates in §3.2 and §3.3
```

> **Verification status.** Nothing here has been exercised on a real phone or tablet. The three modes are driven by the media-query fake in jsdom and by a real resized Chromium in the axe target; `env(safe-area-inset-*)` resolves to 0 in both, so the safe-area padding is verified as _present_ rather than as _correct on a notched device_.

## 12. Troubleshooting

| Symptom                                                                              | Cause and fix                                                                                                                                                                    |
| ------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `npm run lint` fails naming "a hardcoded application breakpoint"                      | A media query spelled one of the two edges directly. Use a `bp.*` mixin, or `MOBILE_VIEWPORT` / `TABLET_VIEWPORT` in TypeScript (§3.3)                                          |
| `check:tokens` fails with "is not 0.02px below any declared minimum"                  | A whole-number maximum was written beside a whole-number minimum, reopening the fractional dead zone (§3.1)                                                                     |
| `check:tokens` fails naming a breakpoint that differs between the two files           | The two copies drifted. They describe one edge and must be the same number (§3.2)                                                                                              |
| A component's styles vanish and the bundle grew                                       | The component `@use`d `tokens` rather than `breakpoints`. Only `breakpoints` is safe to `@use`, because it emits no CSS (§2.1)                                                  |
| The tablet chevron changes the desktop sidebar width                                  | The overlay was routed through `UiStore` instead of the shell's own flag ([Shell and Navigation §5.5](shell-and-navigation.md#55-three-viewport-modes-us-1403))                 |
| axe reports `duplicate-id` on `sidebar-primary`                                       | A second `<app-sidebar>` was declared. There is exactly one, rendered at one of two outlets, and the drawer's copy is `@if`-guarded because the `<dialog>` is always in the DOM |
| The mobile drawer opens and nothing is announced                                      | The `<h2>` was hidden instead of clipped. It is the dialog's accessible name (§4.2)                                                                                             |
| A screen's content is cut off at the bottom and will not scroll                       | The routed host has no scroll container, and `.shell__main` is `overflow: hidden` (§9.2)                                                                                        |
| Tab reaches the content behind the tablet overlay                                     | `inert` came off `.shell__main`. The backdrop stops the pointer and nothing else (§4.1)                                                                                         |
| A picker opens partly off the right edge                                              | `anchoredPosition` was given `window.innerWidth`, or the panel's measured width. Pass `documentElement.clientWidth` and the intended width (§6.2, §6.3)                         |
| The composer's Send button is clipped or the row scrolls sideways below 768px         | A pill in `.composer__controls` regained a fixed width — check the four-level `min-width: 0` chain on the model pill, and that the tools and project labels still collapse (§8.4.1) |
| The chat kebab appears over the projects screen                                       | A publisher did not `clear()` on destroy (§5)                                                                                                                                  |
| Safe-area padding has no effect on a device with a notch                              | `viewport-fit=cover` came off the viewport meta tag; every inset then resolves to 0px (§7)                                                                                      |

## 13. Key files

| Concern                  | File                                                                                                                                                                                                          |
| ------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Breakpoints (SCSS / TS)  | [`src/styles/_breakpoints.scss`](../../enterprise-gpt-ui/src/styles/_breakpoints.scss), [`shared/layout/breakpoints.ts`](../../enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts)                         |
| The shell's modes        | [`features/shell/shell.ts`](../../enterprise-gpt-ui/src/app/features/shell/shell.ts)                                                                                                                          |
| The controlled sidebar   | [`features/shell/sidebar/sidebar.ts`](../../enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts)                                                                                                      |
| The navbar and its seam  | [`features/shell/mobile-navbar/`](../../enterprise-gpt-ui/src/app/features/shell/mobile-navbar/), [`core/ui/shell-chrome.ts`](../../enterprise-gpt-ui/src/app/core/ui/shell-chrome.ts)                         |
| Overlay placement        | [`shared/overlay/anchored-panel.ts`](../../enterprise-gpt-ui/src/app/shared/overlay/anchored-panel.ts)                                                                                                        |
| The drawer               | [`shared/overlay/offcanvas/`](../../enterprise-gpt-ui/src/app/shared/overlay/offcanvas/)                                                                                                                      |
| Safe areas               | [`src/styles/_kit.scss`](../../enterprise-gpt-ui/src/styles/_kit.scss), [`src/index.html`](../../enterprise-gpt-ui/src/index.html)                                                                            |
| The gates                | [`scripts/check-forbidden-apis.mjs`](../../enterprise-gpt-ui/scripts/check-forbidden-apis.mjs), [`scripts/check-tokens.mjs`](../../enterprise-gpt-ui/scripts/check-tokens.mjs)                                 |
| Related reference        | [Shell and Navigation](shell-and-navigation.md), [Accessibility audit](accessibility-audit.md), [Design System](design-system.md), [Administration](administration.md), [Projects](projects.md)                |
