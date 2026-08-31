# Design System

Reference for the presentation layer of the Angular client at `enterprise-gpt-ui/`: the token layer every colour comes from, the theme contract that keeps a dark user from seeing a light frame, the self-hosted type and icons, and the shared component kit. Audience: a developer about to build a screen, who needs to know which pieces already exist, which of them are opinionated, and where the implementation deliberately departs from the design boards.

Companion to [Frontend Foundation](frontend-foundation.md), which covers configuration, error normalization and the store features, and to [the rebuild PRD](../prd/enterprise-ui-rebuild.md), the authority for every `US-xxx` reference below. The visual authority is the Claude Design handoff bundle at `docs/design/`; `docs/design/project/theme.css` is the source of record for colour.

## 1. Overview

> **Scope.** This is the kit, not a screen. EP-2 has since added sign-in, the guarded route tree and sign-out, and EP-3 the signed-in shell, the conversation sidebar and the chat route — the first screens assembled from these pieces, documented in [Shell and Navigation](shell-and-navigation.md). The composer and the transcript are still EP-4. The three components EP-2 contributed to `shared/` are folded into §7: `<app-user-footer>`, `<app-signing-out>`, and the upload drop zone.

Four stories landed together, and they solve one problem each:

1. **Theming** (US-105). Two themes, one token vocabulary, and a pre-paint contract that gets the right one on screen before the first frame (§2, §3).
2. **Assets** (US-109). Three type faces, 75 icons, five multi-colour brand marks (US-418, grown from two) and four brand images, all served from our own origin. The application issues **zero third-party requests at runtime** — no Google Fonts, no CDN, no icon font (§4, §5, §6).
3. **The kit** (US-106). Twenty-odd presentational components with the accessibility contracts written into them rather than left to each screen (§7, §8).
4. **The gates** (US-108). ESLint plus five Node checks that keep all of the above from drifting (§10).

Two rules to internalize before writing any component stylesheet:

- **Consume tokens with `var(--…)`. Never `@use 'tokens'` or `@use 'typography'` from a component.** Both partials emit document-level blocks — `:root` and twelve `@font-face` declarations respectively. Under emulated encapsulation a component re-emits them scoped to its own attribute, where they match nothing and cost bytes in every component that imports them.
- **No Bootstrap JavaScript.** `bootstrap.bundle.js` is not installed and must not be. The overlays are native `<dialog>` plus about 3.9 kB of our own code (§7.2).

### 1.1 Where each piece lives

| Concern | Where |
| --- | --- |
| Token maps, the divergence guard, the `--bs-*-rgb` companions | [`src/styles/_tokens.scss`](../../enterprise-gpt-ui/src/styles/_tokens.scss) |
| Pre-paint theme script, shell colours, font preloads | [`src/index.html`](../../enterprise-gpt-ui/src/index.html) |
| Theme state and the `<html>` writes | [`core/theme/theme-service.ts`](../../enterprise-gpt-ui/src/app/core/theme/theme-service.ts) |
| The stored theme preference, and every other permitted `localStorage` key | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) |
| Stylesheet order, Bootstrap trim, motion | [`src/styles.scss`](../../enterprise-gpt-ui/src/styles.scss), [`src/styles/`](../../enterprise-gpt-ui/src/styles/) |
| `@font-face`, the three stacks, `unicode-range` | [`src/styles/_typography.scss`](../../enterprise-gpt-ui/src/styles/_typography.scss) |
| Icon manifest and the `IconName` union | [`shared/icon/icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/icon-names.ts) |
| Brand mark manifest, sources and `BrandIcon` (§5.4) | [`shared/icon/brand-icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon-names.ts), [`shared/icon/brand/`](../../enterprise-gpt-ui/src/app/shared/icon/brand/), [`shared/icon/brand-icon.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon.ts) |
| Sprite injection | [`core/icons/load-icon-sprite.ts`](../../enterprise-gpt-ui/src/app/core/icons/load-icon-sprite.ts) |
| Switch (§7.8) | [`shared/switch/switch.ts`](../../enterprise-gpt-ui/src/app/shared/switch/switch.ts) |
| Toast state (not presentation) | [`core/notifications/toast-store.ts`](../../enterprise-gpt-ui/src/app/core/notifications/toast-store.ts) |
| Every kit component | [`src/app/shared/`](../../enterprise-gpt-ui/src/app/shared/) |
| Asset and check scripts | [`enterprise-gpt-ui/scripts/`](../../enterprise-gpt-ui/scripts/) |
| The gallery | [`features/ui-kit/`](../../enterprise-gpt-ui/src/app/features/ui-kit/) |

## 2. The token layer

### 2.1 How to consume it

Every colour, radius and duration is a CSS custom property on `<html>`. A component reads it and imports nothing:

```scss
// shared/card/project-card/project-card.scss
.project-card {
  background: var(--surface);
  border: 1px solid var(--bs-border-color);
  color: var(--bs-body-color);
  transition: border-color var(--t-fast);

  &:focus-visible {
    outline: 2px solid var(--focus-ring);
    outline-offset: 2px;
  }
}
```

That is the whole API. Because the properties live on `<html>` and re-resolve on a theme change, a component written this way is themed for free and needs no second stylesheet, no `:host-context`, and no script.

### 2.2 What is in it

[`_tokens.scss`](../../enterprise-gpt-ui/src/styles/_tokens.scss) holds two Sass maps, `$light` and `$dark`, transcribed key-for-key from `docs/design/project/theme.css`, plus `$extra-light` / `$extra-dark` for the properties this application adds. Names keep their original casing — `--btnP-bg` is unusual, but custom properties are case-sensitive and the drift check compares against `theme.css` verbatim.

| Group | Properties |
| --- | --- |
| Bootstrap overrides | `--bs-body-bg`, `--bs-body-color`, `--bs-border-color`, `--bs-secondary-color`, `--bs-link-color`, `--bs-link-hover-color` |
| Surfaces | `--surface`, `--surface-2`, `--muted`, `--active-bg`, `--think-bg` |
| Brand and state | `--brand`, `--accent`, `--ok`, `--warn`, `--warn-bg`, `--warn-border`, `--fail`, `--ring` |
| Chat | `--bubble`, `--bubble-fg` |
| Primary button | `--btnP-bg`, `--btnP-fg`, `--btnP-hover` |
| Theme-swap switches | `--show-light`, `--show-dark` (`inline` / `none`) |
| App additions | `--focus-ring`, `--tooltip-bg`, `--tooltip-fg`, the six `--code-*` syntax colours, and `--code-head-fg` / `--code-head-muted` |
| Motion (in `_motion.scss`) | `--t-fast` (150 ms), `--t-slow` (200 ms) |

Five decisions in that table are worth the paragraph they cost:

- **The `--bs-*-rgb` companions are not decoration.** Bootstrap 5.3 consumes them as `rgba(var(--bs-body-color-rgb), …)` in its `.text-*`, `.bg-*` and `.link-*` opacity utilities. Overriding only the solid values would leave those utilities painting Bootstrap's default blues over a branded palette — latent in the prototype only because it never used them. Four are generated automatically from the solid values.
- **`--focus-ring` is its own token rather than `--accent`.** WCAG 2.1 SC 1.4.11 wants 3:1 against adjacent colours, and `#21A8D8` gives 2.74:1 on `--surface`, 2.62:1 on `--bs-body-bg` and 2.48:1 on `--surface-2` — an accent-coloured ring fails on every light surface in the kit. The light theme uses a darker value on the same brand ramp that clears 3:1 on all three; dark theme keeps the accent, which is already 6.09:1 there.
- **`--tooltip-bg` / `--tooltip-fg` fix a real defect in the boards.** They hard-code the flyout as `#0B1F33` on white, and `#0B1F33` *is* `--bs-body-bg` in dark — so the tooltip would be invisible in the theme it is most used in.
- **`--code-head-fg` / `--code-head-muted` are transcribed literals, identical in both themes.** The Transcript board writes the code-block header bar's Copy control as `#B9CBDC` and its language label as `#8FA9C0`, as literals rather than tokens (US-603). They sit unchanged in both maps for the same reason the `--code-*` syntax palette does: `--code-head` is dark in *both* themes, so text on it never flips. Both clear 4.5:1 there.
- **`--show-light` / `--show-dark` are how assets swap without script.** Both variants of an image render; one is `display: none`. §5 explains why that is worth two downloads.

### 2.3 Two guards you will meet before you meet a bug

**A token defined in one theme and forgotten in the other is a compile error.** `_tokens.scss` walks both merged maps and raises `@error 'Token --x is defined in $light but missing from $dark.'`. Without it the omission is invisible until someone switches themes on a screen nobody checked.

**`npm run check:tokens` diffs the app against the design bundle.** It compares both maps against `theme.css` after normalizing formatting only (`#FFFFFF` equals `#ffffff`, `rgba(33,168,216,.45)` equals the spaced form Prettier writes), never a value. It also checks the two colours per theme that `index.html` duplicates for the pre-paint shell, and that the `localStorage` key is spelled the same way in the inline script and in `PREFERENCE_KEYS.theme` — which US-205 moved out of `ThemeService` into [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts), the application's only permitted `localStorage` call site (§10). A colour changed in one place and not the other is a red build rather than a review comment.

### 2.4 Why the token block sits where it does in `styles.scss`

Statement order in [`styles.scss`](../../enterprise-gpt-ui/src/styles.scss) *is* output order, and three points in it carry weight:

```scss
@import 'typography';           // @font-face and the three stacks
@import 'bootstrap-config';     // configuration, the utilities trim, and `root`
@import 'tokens';               // ← immediately after `root`
@import 'bootstrap-components'; // reboot, type, forms, buttons, transitions, nav, four helpers
@import 'base';
@import 'kit';
@import 'bootstrap-overrides';  // after the components they restyle
@import 'prism';
@import 'bootstrap/scss/utilities/api';
@import 'motion';               // last: its reduced-motion block must defeat everything above
```

The token block's selectors are byte-identical to Bootstrap's own `_root.scss` (`:root, [data-bs-theme='light']` and `[data-bs-theme='dark']`). Equal specificity plus a later source position means the tokens win with **no `!important`** — which only holds while `tokens` is imported directly after `bootstrap/scss/root`.

Everything is `@import` rather than `@use` on purpose: Bootstrap 5.3's partials still share one global `!default` namespace, so its configuration and its component modules have to compile in a single scope. `angular.json` silences the Dart Sass deprecation for exactly that reason.

**Bootstrap is imported module by module.** The whole of `bootstrap.scss` compiles to about 230 kB against a budget the global stylesheet counts towards; [`_bootstrap-config.scss`](../../enterprise-gpt-ui/src/styles/_bootstrap-config.scss) and [`_bootstrap-components.scss`](../../enterprise-gpt-ui/src/styles/_bootstrap-components.scss) take the dozen-and-a-half modules the design actually uses, trim `$theme-colors` from eight to three and cut the utilities API down to a positive list of layout groups. The compiled stylesheet — Bootstrap subset, tokens, kit, Prism and motion together — is **60.32 kB**, against a `styles` budget of 65 kB warn / 80 kB error. Each module's absence is recorded with its measured cost in the header of `_bootstrap-components.scss`; read that before adding one back.

## 3. Theming and the pre-paint contract

### 3.1 The sequence

```text
index.html <head>
  ├─ inline <style>      two colour pairs, duplicated from the tokens
  └─ inline <script>     reads localStorage['egpt.theme'] → matchMedia fallback
                         sets <html data-bs-theme> AND <html>.style.colorScheme
                                    ↓ first paint happens here, already correct
styles.scss loads        tokens resolve against the attribute that is already set
  ↓
provideAppInitializer(() => inject(ThemeService))
  └─ ThemeService reads the same key, applies the same values, and takes over
```

The handover changes nothing on screen, which is the design goal: the script and the service must agree, and `check:tokens` asserts the two things they share — the storage key and the shell colours.

Two details in the script:

- **`style.colorScheme` as well as the attribute.** Without it the user agent paints scrollbars, form controls and the default canvas from `color-scheme: light dark`, which follows the *operating system* — so a user on a light OS with a stored dark preference still gets a light startup shell.
- **`localStorage` access is wrapped in `try`.** Private mode, blocked third-party storage and enterprise policy all throw on access rather than returning `null`.

### 3.2 The single most important line

```jsonc
// angular.json → build → configurations → production
"optimization": {
  "styles": { "minify": true, "inlineCritical": false },
  "fonts": { "inline": false }
}
```

**`inlineCritical: false` is what makes the pre-paint script work at all.** Beasties (the critical-CSS inliner) inlines only the rules that match the *static* HTML it can see at build time. A `data-bs-theme="dark"` set at runtime never appears in that HTML, so no dark rule is ever inlined — and worse, the real stylesheet is then loaded non-blocking. A dark user painted a light frame no matter how early the script ran. Turning the inliner off costs a render-blocking stylesheet on a single-page application that has one; it buys a correct first frame.

`fonts.inline: false` is the companion: it guarantees a stray Google Fonts `<link>` can never be silently inlined at build time and turned into a runtime request to a third-party origin that the `check:forbidden` scan would then be unable to see.

### 3.3 `ThemeService`

```ts
const theme = inject(ThemeService);

theme.preference();       // 'light' | 'dark' | 'system'  — what the user chose
theme.theme();            // 'light' | 'dark'             — what is on screen
theme.set('system');      // records and applies; resumes following the OS
theme.toggle();           // flips the resolved theme and pins the choice
```

- `preference` is **read-only outside the class**. `apply()` is imperative, so a writable signal would let a caller flip `theme()` — and every consumer reading it — while `<html>` kept the old value and nothing was persisted.
- `apply()` is deliberately **not an `effect()`**. It is a DOM write outside Angular's render tree; an effect would defer it to the next flush and entangle it with the exhaustive check-no-changes pass for no benefit. Writing synchronously is what puts the flip in the same frame as the click that caused it.
- The service is instantiated eagerly through `provideAppInitializer`, so ownership of `<html>`'s theme attributes is explicit rather than a side effect of whichever component happens to inject it first. A route with no theme control still has to follow the OS while the preference is `system`.
- **The key is `PREFERENCE_KEYS.theme` (`egpt.theme`), and it no longer lives here.** US-205 moved it into `core/storage/local-preferences.ts`, and `ThemeService` reads and writes it through that module's `readPreference` / `writePreference` — which also absorbed the `try`/`catch` around blocked storage. Changing the key without changing the inline script still costs every user their stored preference exactly once, silently, so `check:tokens` follows it to its new home.

`<app-theme-toggle />` is the control. It renders both glyphs and swaps them with `--show-light` / `--show-dark`, so no script picks an icon; the *label* does read the resolved theme, because a screen reader cannot see a custom property. It carries no `aria-pressed` — the label already states the action, and the pair announces as "Switch to dark theme, not pressed", which is worse than either alone.

### 3.4 Motion

The application defines exactly four keyframes, all from `theme.css`: `blink` (streaming caret), `spin` (spinners), `ringpulse` (jump-to-latest, voice recording) and `ridgedash` (thinking indicator). Anything else that moves uses a `transition` on the `--t-fast` / `--t-slow` scale. This is why Bootstrap's `_spinners.scss` and `_placeholders.scss` are excluded — each would add a fifth.

`_motion.scss` is imported last so its `prefers-reduced-motion: reduce` block, the one place `!important` is warranted, can defeat every animation and transition declared above it.

#### The reduced-motion contract (US-1404)

US-1404 is a **conformance** story, and its finding is the part worth carrying: **the global block is sufficient on its own for every animation the application runs.** That was verified site by site rather than assumed. Floored to one iteration of 0.01ms with no `animation-fill-mode`, each of the four keyframes settles somewhere harmless — `spin` ends at `rotate(360deg)`, which is where it started; both `ringpulse` sites end with no box-shadow, and neither the jump-to-latest control nor the recording pill needs one to be legible, since the pill keeps its tint and its elapsed timer.

**So no redundant `animation: none` overrides were added.** Four sites still carry a local override, and each is there because the floored end-state would be _wrong_ rather than merely still: the `blink` caret would settle invisible, `ridgedash` would leave a blank gap in the ridgeline, the activity spinner keeps a recognisable three-quarter arc, and the toast's drain bar is hidden outright rather than flashed to empty.

Two declarations were added to the block: `animation-delay: 0s` and `transition-delay: 0s`. **A delay survives a floored duration and reappears as a late jump.** Bootstrap's own reboot omits them, which is where this block's shape came from, but that omission is a gap rather than a decision — and nothing in `src/` declares a delay today, which is exactly when it is cheap to close.

Three of the story's four criteria name motion **the application does not have**: there is no typewriter animation, no toast enter/leave slide (`@angular/animations` is not a dependency), and no message-appearance fade-slide. PRD §5 states a third timing, "200ms for message appearance", and it describes an unimplemented affordance — a message is inserted with no transition and no animation. `_motion.scss` now **records that** rather than closing it. Adding an animation inside the story whose whole purpose is to suppress animation is the wrong direction, and a fade-slide on insertion would also disturb US-1402's live-region contract, where the settled turn that replaces the streaming one must not re-announce. `--t-slow`'s stated example is therefore the sidebar width, which is real.

Two gates now hold all of this (§10): `check:forbidden` refuses a fifth keyframe, word-bounded so `@keyframes spinner` cannot pass as `spin`; and `check:tokens` compares all four keyframe bodies against `theme.css`, whitespace and trailing semicolons removed, so a transcription that drifted cannot change how the app moves with every gate still green. `src/styles/motion.spec.ts` pins the third leg: the suppression block itself, the **`@import 'motion'` last position in `styles.scss`** that the whole contract rests on, and that no `animation:` in `src/` names a keyframe nobody defines.

## 4. Typography

Three faces, twelve `woff2` files, all self-hosted.

| Family | Weights | Subsets | Renders |
| --- | --- | --- | --- |
| **Inter** (`$font-sans`, `--bs-font-sans-serif`) | 400, 500, 600 | latin, latin-ext | Body and UI — including user display names and model output, which is why it is the one family carrying latin-ext |
| **Montserrat** (`$font-display`, `--font-display`, `.font-display`) | 600, 700, 800 | latin | Headings, KPI numerals, brand titles |
| **JetBrains Mono** (`$font-mono`, `--bs-font-monospace`, `.font-mono`) | 400, 500, 600 | latin | Token counts, durations, ids, deployment names, trace ids |

`npm run assets:fonts` copies them out of `@fontsource/{inter,montserrat,jetbrains-mono}` 5.3.0 into `public/fonts`, which is gitignored and rebuilt by `prestart` / `prebuild` / `pretest`. A script rather than an `angular.json` asset glob, because a glob that stops matching after an upstream rename ships zero fonts *silently*; the script names all twelve files and fails loudly.

Points that will otherwise cost an hour:

- **`unicode-range` is copied from `@fontsource`'s own per-subset CSS**, so the ranges match the files byte for byte. A character outside every listed range falls back to the next family in the stack rather than downloading a face that cannot render it — which is what makes the four latin-ext files free until an accented character actually appears.
- **`font-display: swap`, not `optional`.** A missing glyph for a fraction of a second beats an invisible one, and the two critical faces are preloaded anyway.
- **`index.html` preloads Inter 400 and Montserrat 600 only** — the faces on the path to first meaningful paint. `crossorigin` is required on those links **even same-origin**: fonts are always fetched in CORS mode, and omitting it makes the browser issue a second, unused request.
- **`$font-path` is root-relative (`/fonts`).** The build resolves every non-root `url()` token as a module and fails on one it cannot find, and these files are copied verbatim into `public/`. A sub-path deployment needs `--base-href` and `deployUrl`, as it already would for the MSAL redirect.
- **The partial is named `_typography`, not `_type`.** `src/styles` is on the Sass load path, so `@import 'type'` would resolve to it and silently shadow `bootstrap/scss/type`.

## 5. Icons

### 5.1 How it works

The icon font is **not** shipped: its stylesheet alone is roughly 72 kB, and it would carry 2,078 glyphs to render the 75 the design uses. Instead:

```text
shared/icon/icon-names.ts   75 sorted names + the IconName union   ← checked in
        │  npm run assets:icons
        ▼
public/icons/sprite.svg     one <symbol> per glyph, fill=currentColor   ← gitignored
        │  loadIconSprite(), in parallel with the config fetch in main.ts
        ▼
<div aria-hidden> prepended to <body>   → <use href="#bi-search"> resolves same-document
```

The same sprite file carries a second, differently-built set of symbols — the multi-colour brand marks (§5.4).

**The sprite is injected, not referenced.** An externally referenced sprite (`sprite.svg#bi-search`) is a separate document, and `currentColor` inside it resolves against *that* document's initial colour — black — instead of inheriting from the host element. Every icon would render black in both themes.

It is also an asset rather than a bundled string: inlining it through the build would put roughly 34 kB into the initial JS chunk, where it counts against the budget; in `public/` it costs nothing and is fetched once, concurrently with the `config.json` fetch that already gates bootstrap, so there is no icon pop-in on first paint. `loadIconSprite()` never rejects — an icon-less app is degraded, not broken, and is not a reason to route into the fatal shell. It also refuses markup that does not start with `<svg>`, because a host with SPA fallback routing answers a missing file with `index.html` and a `200`, and injecting that would put a second `<app-root>` into the page.

### 5.2 Using one

```html
<app-icon name="bi-search" />                  <!-- decorative: no accessible name -->
<app-icon name="bi-trash3" label="Delete" />   <!-- content: exposed as an image -->
```

Size it with `font-size` on the host or an ancestor; the component is sized in `em` precisely so no size input is needed, matching how every design board sets icon size.

Supplying `label` promotes the icon from decoration to content. Leave it unset whenever the icon sits beside text that already names the control — a labelled icon inside a labelled button is announced twice.

### 5.3 Adding a glyph

1. Add the name to `ICON_NAMES` in [`shared/icon/icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/icon-names.ts), **in sorted order**.
2. Run `npm run assets:icons`.

```bash
$ npm run assets:icons
icon sprite OK — 75 glyphs + 5 brand marks, 37.6 kB → public/icons/sprite.svg
```

The build refuses a name that is malformed, duplicated, out of order, or absent from `bootstrap-icons` 1.13.1 — the last with the URL to check the spelling against. Adding the name without rebuilding leaves the glyph *typed* but absent from the sprite, where it renders blank; `prestart`, `prebuild` and `pretest` all rebuild it, so that only bites someone serving `dist/` by hand.

Three layers catch a wrong name, deliberately, because each sees something the others cannot:

| Layer | Catches |
| --- | --- |
| `strictTemplates` + the `IconName` union | `<app-icon name="bi-typo">` and any expression not typed `IconName` — at compile time |
| `npm run check:icons` | A raw `class="bi bi-…"` in a template, or a `bi-*` literal in TypeScript flowing into a class binding |
| A dev-only `effect` in `Icon` | A name assembled at run time (`'bi-' + kind`), which neither of the above can see |

### 5.4 Brand marks: a second lane in the same sprite (US-418)

A tool server registered with an icon ([Administration §11.3.1](administration.md#1131-icon-a-select-a-live-preview-and-a-key-nobody-typed-us-1210)) needs its own logo, not a `bi-*` glyph tinted to `currentColor` — a brand mark has to keep the colours its owner specifies. `build-icon-sprite.mjs` therefore reads a **second** manifest, [`shared/icon/brand-icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon-names.ts), against sources checked in under `shared/icon/brand/` rather than resolved from `node_modules`, and emits those `<symbol>` elements into the same `sprite.svg` **without** the `fill="currentColor"` rewrite the `bi-*` lane gets. `BRAND_ICON_NAMES` fails the build if it is out of sorted order, exactly like `ICON_NAMES` (§5.3) — its diffs have to stay readable the same way.

Five names today, `brand-azure`, `brand-context7`, `brand-github`, `brand-microsoft` and `brand-salesforce`. Four carry a brand hex, each kept at its source's native `viewBox` — a `<symbol>` establishes its own viewport, so a differently-scaled source still renders at whatever `font-size` the caller sets: the Microsoft mark is bootstrap-icons' own `microsoft.svg`, its four-subpath `d` split one path per quadrant and coloured with the brand hexes so it sits at the same optical weight and the same 16-unit grid as every other glyph; Azure and Salesforce are Simple Icons' CC0 marks on their native 24-unit grid, coloured Azure blue (`#0078D4`) and Salesforce blue (`#00A1E0`) respectively; Context7's is the project's own MIT-licensed mark, kept at its native 28-unit `viewBox`. **`brand-github` is the one exception, and the only mark in the set on `fill="currentColor"` rather than a hex** — the Octocat is monochrome by GitHub's own brand guidelines, shipped in black or white, and a hardcoded `#181717` would be invisible against the dark theme's surface.

```html
<app-brand-icon name="brand-azure" />                      <!-- decorative -->
<app-brand-icon name="brand-context7" label="Context7" />  <!-- content -->
<app-brand-icon name="brand-github" />                     <!-- decorative, currentColor -->
<app-brand-icon name="brand-microsoft" />                  <!-- decorative -->
<app-brand-icon name="brand-salesforce" />                 <!-- decorative -->
```

All five are in the `/ui-kit` gallery's Brand marks row.

`<app-brand-icon>` ([`shared/icon/brand-icon.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon.ts)) is a **sibling** of `Icon`, not a widening of it: `icon.scss` forces `fill: currentColor` on the sprite reference, which is exactly what a brand mark must not inherit, so the two components — and the two manifests behind them — stay independently checkable. `strictTemplates` rejects a misspelled `name` at compile time for a static reference; the one run-time lookup, `mcpBrandIcon` resolving a server's stored `iconKey` to a `BrandIconName`, is covered instead by a dev-mode `effect` that logs a miss rather than failing silently, the same pattern §5.3's table uses for `Icon`.

`npm run check:icons` is **not** extended to the brand lane: it exists to catch a raw `class="bi bi-…"` the type system cannot see, there is no icon-font class for a brand mark to write by hand, and scanning for a `brand-` prefix would false-positive on every unrelated `brand-logo__*` class from §6. `strictTemplates`, the dev-mode guard above, and the sprite-build's own manifest-vs-source check (§5.3's malformed/duplicate/missing-source failures, run separately per lane) cover it instead — the sprite spec additionally asserts the two lanes are disjoint and that only the `bi-` lane carries `currentColor`.

## 6. Brand assets

`npm run assets:brand` turns the design bundle's PNGs into WebP: **732 kB → 81 kB**, four files in `public/brand`. Unlike fonts and icons it runs **on demand and its outputs are committed**, which keeps `sharp`'s native binary and the `../docs` path off the build's critical path — worth about 55 kB of binaries in git that change approximately never. One size per variant, at 2× the largest place it renders; a `srcset` ladder would trade a few decoded pixels for a second request, which is the wrong way round at this size.

```html
<app-brand-logo alt="Andes Software Solutions" />   <!-- wordmark lockup -->
<app-brand-logo variant="mark" />                   <!-- the mark alone, decorative -->
```

`BrandLogo` renders **both** theme variants and hides one with `--show-light` / `--show-dark`, so no script reads the theme to choose an asset. They carry the *same* `alt`: a `display: none` element is out of the accessibility tree, so exactly one is ever exposed and a screen reader never reads the wordmark twice. The accepted cost is that a hidden `<img>` is still fetched — about 40 kB for the pair, the cheaper side of the trade against a `background-image` version that would have to give up `alt` altogether.

`<app-ridgeline variant="divider|thinking|empty|auth" />` is the Andes motif: **inline** SVG, not an image file, so its stroke inherits `--muted` and `--accent` and re-tints on a theme change with no second asset and no script. The boards draw seven hand-tuned path pairs; four variants cover all of them, because `viewBox` scaling reproduces the size variants and thins the stroke with them, which is what the boards do by hand.

## 7. The component inventory

Everything lives under [`src/app/shared/`](../../enterprise-gpt-ui/src/app/shared/) and is presentational: **it defines no store and makes no HTTP call**. Components are standalone, `OnPush`, and follow the v21 convention (`foo.ts` / `foo.html` / `foo.scss`, no `.component` suffix). All inputs are signal inputs; `model()` marks a two-way binding.

Two clarifications, because "presentational" is doing precise work there:

- **State that several unrelated screens share lives in `core/`, and `shared/` injects it.** Toast *state* is `core/notifications/toast-store.ts`, written from interceptors, guards and feature stores; `ToastRegion` injects it. That direction — `shared → core` — is the sanctioned one.
- **Three components inject a `core/` singleton directly rather than taking inputs**: `ThemeToggle` (`ThemeService`), and since US-205 `UserFooter` (`SessionStore`, `AuthService`). Each is a singleton by construction, and threading inputs and outputs down from whichever screen hosts it would make every future host re-derive the same initials and re-wire the same sign-out call. Everything else takes inputs.

### 7.1 Accessibility primitives — `shared/a11y/`

Functions, not components, so overlays can share exactly one set of semantics.

| Export | Signature | Purpose |
| --- | --- | --- |
| `tabbableWithin` | `(root: Element) => HTMLElement[]` | Focusable descendants in DOM order. Skips `disabled`, `hidden`, `tabindex="-1"`, and anything under `[inert]` or `[aria-hidden="true"]` — both remove a whole subtree, so both are checked against every ancestor |
| `captureFocusOrigin` | `(document: Document) => () => void` | Records `activeElement` now, returns a function that puts focus back — and does nothing if the origin is gone, which is the common case when the invoker was a row's kebab button and the action deleted the row |
| `onDismiss` | `(el, handlers, signal) => void` | Wires Escape, outside pointer, and scroll in one place |

`tabbableWithin` deliberately does not consult `getComputedStyle`: jsdom performs no layout, so a visibility check would be a lie in every unit test and a per-element style read in the browser. Overlays here hide by unmounting, so there is nothing for it to catch.

`onDismiss` makes three choices worth knowing: Escape is listened for **on the overlay**, so a menu opened inside a modal closes only the menu; outside clicks use `pointerdown` with `composedPath()`, so a press that starts inside and ends outside does not dismiss and a click inside a shadow tree still counts as inside; and every listener is registered against one `AbortSignal`, so aborting it in `DestroyRef.onDestroy` removes all of them.

### 7.2 Overlays — `shared/overlay/`

Bootstrap's modal, offcanvas, dropdown and tooltip together are **86 kB** (26.7 kB of CSS plus Popper and the JS bundle). They are replaced by native `<dialog>` and roughly **3.9 kB** of our own CSS and TypeScript. The saving is real but the reason is correctness: the top layer inerts the page for real and supplies `aria-modal` and a cancellable Escape, none of which a `focusin`-cycling trap can match.

| Component | Key inputs | Outputs | Notes |
| --- | --- | --- | --- |
| `<app-modal>` | `open` (model), `heading` **required**, `size` `sm`&#124;`md`&#124;`lg` (420/460/520 px), `dismissible`, `busy` | `closed` | The kit owns the heading so `aria-labelledby` cannot be forgotten. Body is the default slot; actions project into `[modalFooter]` |
| `<app-offcanvas>` | `open` (model), `heading` **required**, `side` `start`&#124;`end`, `width` (`'420px'`), `dismissible`, `busy` | `closed` | Mechanically identical to the modal, pinned to an edge by `margin`. Footer slot `[offcanvasFooter]` |
| `<app-menu>` | `label` **required**, `icon` (`IconName`), `align` `start`&#124;`end`, `hint` (tooltip text for a disabled trigger, US-1502), `open` (model) | — | Items project as `<button appMenuItem>`; `<hr appMenuSeparator />` divides them. Returns focus to the trigger if a consumer clears `open` without calling `close()` |
| `[appTooltip]` | `appTooltip` **required** (text), `appTooltipPlacement` `right`&#124;`top`&#124;`bottom` | — | Directive. Sets `aria-label` — see §8.3. An empty `appTooltip` is a no-op: no attribute, no listener, no flyout (US-1502, since `Menu` binds `hint ?? ''` on every trigger). The flyout is `position: fixed`, placed from the host's measured box and clamped to a 12px viewport gutter — not offset in CSS from a `position: relative` host — because these controls sit routinely inside an `overflow: hidden` ancestor, which clips an absolutely-positioned flyout however high its `z-index`. `Menu`'s own `hint` derives `bottom`&#124;`top` from its `direction` input rather than hardcoding `top` |

`dismissible` and `busy` combine: a dismissible dialog with a save in flight refuses Escape and backdrop clicks until it lands.

Two implementation notes that read as odd and are not:

- **The menu is hand-rolled rather than Bootstrap's dropdown**, which costs 35.7 kB because it bundles Popper. Popper exists to flip a menu around a viewport edge; every menu in this design is anchored to its own row, so a `position: fixed` panel placed from the trigger's rect does the same job — and escapes the sidebar's `overflow-y: auto`, which would clip an absolutely-positioned panel. It closes on scroll rather than repositioning, because a fixed panel that follows a scrolling row is more distracting than one that gets out of the way. Tab **leaves** the menu instead of cycling: this is a menu button, not a dialog, and the page behind it is still live.
- **Initial focus uses `data-modal-autofocus`, not the native `autofocus` attribute.** The native one means "focus this when the document loads", which is disorienting and is why lint bans it; this one means "focus this when the dialog opens", which is a different and correct thing to want. The fallback chain is explicit marker → first tabbable → the heading, which carries `tabindex="-1"` precisely so it can be the last resort. A dialog whose focus lands on `<body>` is one the screen reader never announces.

### 7.3 Feedback — `shared/feedback/`

| Component | Key inputs | Outputs | Use when |
| --- | --- | --- | --- |
| `<app-toast-region>` | — (injects `ToastStore`) | `retried` (toast id) | Mounted **once**, in the app shell. Never per route (§8.2) |
| `<app-toast-item>` | `toast` **required** | `dismissed`, `retried` | Layout only; the region owns the live regions and the queue |
| `<app-error-panel>` | `error` (`AppError`) **required**, `heading` **required**, `headingLevel` `1`&#124;`2`&#124;`3`&#124;`null`, `canRetry`, `retryLabel` | `retried` | A surface that failed to load. Names the `traceId` and offers Retry |
| `<app-unavailable-panel>` | `heading` **required**, `message` **required** | — | A capability this deployment does not have |
| `<app-empty-state>` | `heading` **required**, `message`, `illustration` `ridgeline`&#124;`none`, `headingLevel` `2`&#124;`3`&#124;`4` | — | The request succeeded and the answer was zero rows. Action projects into `[emptyStateAction]` |
| `<app-skeleton>` | `variant` `text`&#124;`block`&#124;`circle`, `width`, `height`, `lines` | — | Loading placeholder |
| `<app-signing-out>` | — | — | The full-page state `App` swaps in while sign-out runs. Focuses its own heading on render |

**Three failure surfaces, deliberately distinct, and the type signatures enforce the distinction.** `UnavailablePanel` has **no** `error` input, **no** retry output and no action slot, because nothing there is worth retrying — a missing API does not come back because the user pressed a button. `EmptyState` means the request succeeded and returned nothing. Only `ErrorPanel` says something went wrong that might not go wrong next time. Picking the wrong one is the difference between "we have not built this", "your data is empty" and "try again", which the deleted client routinely conflated.

Error copy comes from [`core/errors/error-message.ts`](../../enterprise-gpt-ui/src/app/core/errors/error-message.ts) — `userMessage()` and `traceLine()` — never from the call site, so a panel and a toast raised by the same failure say the same thing.

`ErrorPanel.headingLevel` defaults to `null`, which leaves its heading line as plain emphasised text — right when the panel sits inside a screen that already has a heading (frame `4k`). A full-page state where the panel *is* the page passes a level; the session-error screen of frame `6c` passes `1`. It is applied as `role="heading"` plus `aria-level` rather than by switching the element, so one component does not fan out into six template branches, and the element takes `tabindex="-1"` so the route can move focus to it on arrival.

`Skeleton` is always `aria-hidden` (the container that swaps it for content carries `aria-busy`; a screen reader reading out a row of grey boxes helps nobody) and is static, with no shimmer, which is also what the reduced-motion path would collapse to.

**`SigningOut` is a full page rather than an overlay, and a component rather than markup inline in `App`.** Both are deliberate. Full page, because a blocked sign-out redirect leaves it on screen for MSAL's 30-second timeout, and a frozen shell behind a spinner reads as a hang where an honest full-page state reads as work in progress. A component, because `focusOnRender` needs the heading to exist: a `viewChild.required` declared in `App` would resolve to nothing at `afterNextRender` time, since the `@if` has not instantiated the heading yet. Without it the button the user just pressed is removed from the DOM and focus drops to `<body>`. `focusOnRender` itself moved from `features/auth/` to [`shared/a11y/`](../../enterprise-gpt-ui/src/app/shared/a11y/focus-on-render.ts) for this component's sake.

**Toasts.** `ToastStore` is the only way one appears:

```ts
const toasts = inject(ToastStore);

toasts.success('Conversation renamed');
toasts.info('Upload queued', 'It will be ready in a moment.');
toasts.warning('Two documents were skipped');
toasts.fromError(error, 'Retry');   // AppError → toast, or null when deliberately silent
toasts.dismiss(id);
```

`fromError` routes every failure through `shouldNotify` / `userMessage` / `traceLine`, which is what keeps a cancelled turn from being announced as an error and keeps the toast and the panel consistent. Auto-dismiss is a typed table — success and info 5 s, warning and error **0, meaning they persist** — declared `as const satisfies Record<ToastTone, number>`, so a new tone cannot be added without deciding whether it disappears on its own. The design's rule, "an error that disappears before it is read is a defect", is a property of the type system rather than a convention.

Timer handles live in `withProps`, not in state: a `Map` in `withState` is either mutated in place (no signal write, so nothing re-renders) or replaced (re-rendering every toast on each schedule and each cancel).

### 7.4 Data surfaces — `shared/data/`

| Component | Key inputs | Outputs | Notes |
| --- | --- | --- | --- |
| `<app-data-table>` | `rows`, `columns`, `trackKey`, `rowLabel`, `label` — all **required**; `loading`, `skeletonRows`, `summary`, `pendingIds`, `selectable`, `selectedIds` (model), `headerSelectAll` | — | Slots `[tableNotice]`, `[tableEmpty]`, `[tableFooter]` |
| `ng-template appTableCell` | `appTableCell` (column key), `appTableCellFor` (type anchor) | — | Typed cell template |
| `<app-card-row>` | — | — | Mobile stand-in for a row. Slots `cardRowLead/Title/Subtitle/Meta/Badges/Trailing/Actions` — `Meta` renders as a labelled `<dt>`/`<dd>` list keyed by each column's header; `Trailing` sits beside the title for a single icon control; `Actions` is the card's full-width foot, for a row of labelled buttons |
| `<app-paginator>` | `page`, `totalPages`, `totalCount`, `pageSize`, `itemLabel` — all **required** | `pageChanged` | Numbered pager for server-paged screens |
| `<app-bulk-action-bar>` | `count` **required**, `note` | `cleared` | Actions project as the default slot. `position: fixed`, so it caps and wraps itself (SC 1.4.10) — see below |
| `row-selection.ts` | `toggleRow`, `selectRows` | — | Every one returns a **new** `Set` |

`DataTable` imports no store. Everything arrives as inputs, which is what lets it serve `withOffsetPagination` (the footer slot), `withPendingIds` (`pendingIds`) and `withClientQuery` (the notice slot) without knowing any of them exist.

```html
<app-data-table
  [rows]="store.results()"
  [columns]="columns"
  [trackKey]="byId"
  [rowLabel]="byName"
  label="Conversations"
  [loading]="store.isPending()"
  [pendingIds]="store.pendingIds()"
  [(selectedIds)]="selected"
  selectable
>
  <p tableNotice>{{ store.resultsPartialReason() }}</p>

  <ng-template appTableCell="name" [appTableCellFor]="store.results()" let-row>
    <a [routerLink]="['/chat', row.id]">{{ row.name }}</a>
  </ng-template>

  <app-empty-state tableEmpty heading="No conversations yet" />
  <app-paginator tableFooter … />
</app-data-table>
```

`appTableCellFor` exists **only** to give `T` an inference site — the same trick `NgFor` uses — so `let-row` is typed as the row and `row.nmae` is a compile error rather than `undefined` on screen. Bind the row array to it; it is never read at run time.

`TableColumn<T>` is `{ key, header, width, align?, headerHidden?, text?, mobile?, hideOnTablet? }`, where `width` is a CSS grid track (`'40px'`, `'1.2fr'`, `'minmax(0, 1fr)'`), `mobile` names which card slot the column moves to below 768 px — `title` | `subtitle` | `meta` | `badges` | `trailing` | `actions` | `hidden` — and `hideOnTablet` drops the column from the **table** between 768 and 1023px, where the tracks a wide table needs would otherwise run past the pane. Below 768px the table renders `CardRow`s instead, because the boards **restructure** the row at that width rather than reflowing it — and a `role="table"` that looks like a list of cards is a lie no stylesheet can fix. A card reads every column regardless of `hideOnTablet`: a phone is a different layout, not a narrower tablet, so nothing a card can already hold needs to be dropped for it. See [Administration §3.3.1](administration.md#331-six-defects-in-the-card-anatomy-and-four-bugs-found-while-fixing-them) for what the `meta` and `trailing` slots are for and the defects fixing them corrected.

Three performance constraints are baked in and are easy to undo by accident: row contexts and the mobile slot buckets are memoized in `computed`s rather than built in the template, because binding `[ngTemplateOutletContext]` to a method call allocates a new object on every check, which `provideCheckNoChangesConfig({ exhaustive: true })` fails immediately — correctly, since in a zoneless app it also means the view is dirtied on every pass. And `contentChildren` uses `descendants: true`, because a consumer's cell templates are frequently wrapped in an `@if` for a permission-gated column; without it the template is simply not found and the column silently renders blank.

**Every selection updater returns a new `Set`.** `set.add(id)` followed by writing the same reference back performs no signal write at all: the view never re-renders and the bug looks like a dead checkbox. `core/state/with-pending-ids.ts` documents the same rule for the same reason, and the specs assert `not.toBe`.

Three inputs exist for a caller whose board disagrees with the component's defaults, all added by EP-7 (US-702, US-704) and each closing a specific hole:

- **`summary`** replaces the row count the table announces and describes itself by. A paginated caller knows something the table cannot — how many rows exist beyond the ones it was handed — so it hands over "Showing 25 of 312 conversations" and the default "25 results" stands down. **The caller's own visible copy of that string must then be `aria-hidden`**, or a screen reader hears the count twice, from two derivations that will eventually disagree.
- **`headerSelectAll="false"`** drops the checkbox from the header row while keeping the 40 px track the body rows are laid out against, for a board that draws select-all in its own toolbar. Two controls bound to one state is a defect, and below 768 px there is no header row to hold one at all.
- **Selection now exists below the breakpoint**, through `CardRow`'s previously unused `[cardRowLead]` slot. Without it a caller that set `selectable` silently got a table on a laptop and an unselectable list on a phone.

One projection trap belongs to the footer slot rather than to any input: **a conditional footer must put its `@if` _inside_ a stable projected wrapper.** Content projection matches static nodes, so an `@if` around the element compiles to an `ng-template` carrying no `tableFooter` attribute, and — with no catch-all slot here — it is dropped with no error. `.table-footer` therefore keys its gap on `:has(> :empty)`, since the slot itself is never empty again once a wrapper is projected into it.

**`BulkActionBar` caps and wraps itself, rather than leaving it to call sites.** It is `position: fixed`, so nothing downstream can rescue it from overflowing a narrow viewport: it wraps, centres its children and holds `max-width: calc(100vw - 24px)`, and the note — the longest and least urgent child — is the only one allowed to take a second line, ahead of the count or the actions.

### 7.5 Badges, chips and cards

| Component | Key inputs | Outputs | Purpose |
| --- | --- | --- | --- |
| `<app-status-dot>` | `tone` (`ok`&#124;`warn`&#124;`fail`&#124;`muted`&#124;`accent`&#124;`brand`), `label` **required**, `labelHidden`, `size` | — | Provider dots, activity state |
| `<app-kind-badge>` | `kind` **required** | — | A `ToolKind` from the stream contract: "MCP tool", "Agent", "Function" |
| `<app-permission-badge>` | `name` **required**, `managedBy` | — | A grant; `managedBy` marks one the administrator cannot revoke here |
| `<app-source-badge>` | `source` `uploaded`&#124;`generated` | — | Distinguishes an uploaded file from an assistant-produced one |
| `<app-attachment-chip>` | `attachment` **required**, `removable`, `removeAction`, `downloadable`, `downloading`, `generated` | `removed`, `retried`, `downloaded` | One attached file, five upload states plus a generated variant |
| `<app-project-card>` | `name`, `updatedLabel`, `link` — **required**; `description`, `favorite`, `pending` | `favoriteToggled` | Project tile; row menu projects into `[cardMenu]` |

`KindBadge` renders the kind **alone**. The stream contract keeps `displayName` and `kind` apart precisely so a card reads "Create issue" + "MCP tool" — with "Jira Cloud" as the `source` subtitle beneath — rather than "Calling Jira Cloud MCP", and composing them here would put the kind word back. An unrecognized kind renders as itself rather than as nothing. The label beside the badge is not the raw `displayName` for an MCP activity: the chat feature derives it in `domain/stream/activity-label.ts`, which is a feature concern and not the kit's ([Answer Rendering §10.3](answer-rendering.md#103-the-running-commentary-is-a-pure-function-and-it-lives-outside-the-transcript)).

`AttachmentChip`'s state is a **discriminated union**, not five booleans:

```ts
type AttachmentState =
  | { kind: 'uploading'; percent: number }
  | { kind: 'processing'; subStatus: string }
  | { kind: 'ready' }
  | { kind: 'unsupported'; reason: string }
  | { kind: 'unknown' };   // a 404 on the status endpoint after the retention window
```

`@switch` over `kind` is then exhaustive and "uploading *and* expired" is unrepresentable — the same modelling discipline `withRequestStatus` established, for the same reason: the deleted client tracked those flags independently and got it wrong. The `unknown` arm is load-bearing rather than cosmetic: an expired status must not read as a failure, because nothing failed and there is nothing to retry. Its copy says so — "Status is only kept for a limited time" — and it offers no Retry.

A sixth input, `generated`, sits outside that union rather than as a state within it: a file the assistant made is outside the upload lifecycle the five states describe, and is always finished, so it bypasses the `@switch` entirely at the template's top level. Its download control uses `[attr.aria-disabled]`, never `[disabled]`, while a link is in flight — disabling the control the user just activated hands focus to `<body>` under the HTML focus-fixup rule and it never returns. See [Generated Files: Storage and Delivery §8.1](../file-agent/generated-files.md#81-the-client-side).

`ProjectCard`'s title is a real `<a routerLink>` rather than a clickable card, so the tile is not one giant unlabelled button and the link stays reachable, focusable and middle-clickable.

### 7.6 Form, navigation and layout

| Export | Key inputs | Outputs | Notes |
| --- | --- | --- | --- |
| `<app-search-input>` | `value` (model), `label` **required**, `placeholder`, `debounceMs` (300), `busy` | `searched` | Debounced; Escape clears. `focusField()` puts the caret in it |
| `<app-pill-subnav>` | `items` (`PillItem[]`) **required**, `ariaLabel` **required** | — | The admin area's horizontal navigation below 768 px |
| `<app-user-footer>` | `compact` | — | Frame `3a`'s sidebar footer: initials avatar, display name, `<app-theme-toggle>`, sign out. Injects `SessionStore` and `AuthService` |
| `injectMediaQuery` | `(query: string) => Signal<boolean>` | — | Must be called in an injection context |

`UserFooter` lives in the sidebar footer since US-301, so it needs no session gate of its own — the shell renders only behind `sessionGuard`. Its `compact` input stacks it into the collapsed 60 px strip, where frame `3b` draws the avatar alone; the theme control and sign out are **kept** there as icon buttons, because a collapsed sidebar is a persisted state and hiding them would make signing out require expanding first. In that width the display name is carried off-screen with `visually-hidden` rather than dropped, and the avatar stays `aria-hidden` either way — otherwise a screen reader reads the initials and then the name they abbreviate. Three further decisions: the initials come from the composed `fullName` and fall back to one letter for a single-word name rather than rendering a stray comma; **sign-out asks for no confirmation**, matching the board — nothing is lost by signing out, since conversations are on the server, so a dialog would be friction guarding nothing; and the button latches disabled while sign-out runs, so a second press has nothing to press rather than merely being ignored.

`SearchInput`'s label is **required and rendered as a real `<label for>`**. A placeholder is not an accessible name: it disappears the moment the user types, and several screen readers do not announce it at all. The in-flight field text is a `linkedSignal` derived from `value` — writable state derived from an input, which is exactly what `linkedSignal` is for — so clearing the filter from an empty state or restoring a query from the URL puts the field back in step with no extra pass. Debouncing is a `setTimeout` inside an `effect` with `onCleanup`, which needs no `rxjs-interop` import and which `vi.useFakeTimers()` drives directly.

`injectMediaQuery` writes a signal from the `change` listener, which is the zoneless-correct way to notify Angular of something that happened outside its own event handling — no `ApplicationRef.tick()`, no zone patch.

### 7.7 Upload — `shared/upload/`

| Export | Key inputs | Outputs | Notes |
| --- | --- | --- | --- |
| `[appFileDropTarget]` | `appFileDropTarget` **required** (boolean) | `filesDropped` (`readonly File[]`) | Directive. `exportAs: 'appFileDropTarget'` exposes `isDragOver()` |
| `<app-drop-overlay>` | `label` (`'Drop file(s)'`) | — | Frame `2a`'s dashed affordance. `aria-hidden` |

```html
<div [appFileDropTarget]="session.canUploadFiles()" (filesDropped)="attach($event)" #drop="appFileDropTarget">
  <textarea …></textarea>
  @if (drop.isDragOver()) { <app-drop-overlay /> }
</div>
```

**The split between `@if` and the directive is the design decision here, and it generalizes.** A permission-gated *affordance* is `@if` — absent, not disabled — which is the same answer US-203 gave the Admin navigation entry. The directive exists only for the one thing `@if` cannot express: an element that must still render and simply not react. The prompt box has to accept typing whether or not the user may upload; it just must not accept a file. Reach for the directive only when that is genuinely the shape of the problem.

Two consequences for anyone using it:

- **The permission is a required input, never an injected `SessionStore` read.** That keeps the authorization decision visible at the call site and keeps the directive usable for a drop zone that has nothing to do with uploads. Required, so an unbound directive is a `strictTemplates` compile error rather than a silently open drop zone.
- **The overlay is `aria-hidden` on purpose.** A drag is a pointer gesture with no keyboard equivalent, so no assistive-technology user ever has this element on screen; the accessible path to the same outcome is the attach button beside it.

`DropOverlay` is a separate component from the directive because the directive owns state and not pixels: the overlay is specified down to the token, and a component is reviewable against the board where DOM assembled inside a directive is not. US-801's composer and US-902's project files panel render the same one.

The mechanics — the depth counter rather than a boolean, the `AbortController`-scoped listeners, the `types` filter that keeps a text drag working, and the window-level `providePreventDropNavigation()` that stops a missed drop replacing the application — are in [Authentication and Session §10](authentication-and-session.md#10-upload-gated-on-the-grant-us-204), because they exist to satisfy a permission criterion rather than a visual one.

### 7.8 Switch — `shared/switch/` (US-417)

`<app-switch [checked]="…" />` draws an on/off track and thumb and **nothing else**: no role, always `aria-hidden`, state supplied by the caller. That split exists for one reason — `role="switch"` is not an allowed owned element of `role="menu"`, which permits only `menuitem`, `menuitemcheckbox`, `menuitemradio`, `group` and `separator`. A native `<input type="checkbox" role="switch">` is therefore not a valid child of the composer's Tools menu, so the row's own `menuitemcheckbox` button keeps `aria-checked` and `app-switch` only draws what that state means. Where a real form control is available instead — `ModelFormDialog`'s Entra-only fields, for instance — use Bootstrap's own `.form-check.form-switch` with `[formField]`, not this component; `app-switch` matches its geometry so the two read as one idiom rather than becoming a second mechanism.

No new design token: the off track is transparent with a `--muted` border and thumb, the on track is `--btnP-bg` with a `--btnP-fg` thumb — 5.27:1 on `--surface` and 4.76:1 on `--surface-2`, against SC 1.4.11's 3:1 non-text minimum. State is carried by the thumb's *position* as well as its fill, so it survives SC 1.4.1 (colour is not the only channel), reduced motion, and forced-colours mode; the transition is one of the four `_motion.scss` already declares, not a fifth keyframe `check:forbidden` would reject. `check-tokens.mjs`'s `FOCUS_SURFACE_EXCLUSIONS` gained `btnP-fg`, newly painted as a background here rather than the text colour it usually is.

Its only consumer today is the Tools menu ([Frontend Foundation](frontend-foundation.md), the composer), which is also why `bi-square` and `bi-check-square-fill` left the icon manifest (§5.3) — the checkbox-glyph pair they drew was that row's only user.

## 8. Accessibility contracts

The PRD's **accessibility precedence** rule (§5) states that where the boards and the PRD disagree on accessibility, the PRD wins; the prototypes carry known gaps that the rebuild corrects rather than reproduces. §9 lists what that authorised. This section is what the kit guarantees.

### 8.1 Focus

- **In.** Opening a modal or offcanvas focuses `[data-modal-autofocus]`, else the first tabbable element, else the heading (§7.2). Opening a menu with ArrowDown focuses the first item; with ArrowUp, the last.
- **Within.** A `showModal()` dialog gets real inertness from the top layer. The menu roves focus with Arrow / Home / End and gives Tab back to the page.
- **Out.** `captureFocusOrigin` records the invoker before the overlay opens and restores it on close **and on destroy**. The destroy path is not belt-and-braces: `dialog.close()` queues its event, so a modal closed by a route change would have its `(close)` binding torn down before the event fired, and focus would drop to `<body>`. US-1401 found that guard **unfalsifiable in the suite**: [`src/testing/dialog-polyfill.ts`](../../enterprise-gpt-ui/src/testing/dialog-polyfill.ts) dispatched `close` synchronously, so deleting the restore left every spec green. The shim now queues the event, as the platform does, and deleting the restore fails `modal.spec.ts`.
- **Visible.** `--focus-ring` at 2px with a 2px **offset**, on a token chosen for 3:1 contrast on every surface in the kit (§2.2) — and, since US-1401, measured against each of those surfaces by `check:tokens` rather than claimed by a comment (§10).

The first three shipped with the kit. **Visible** is the one US-1401 had to build out, because five indicators were failing SC 1.4.11 and the one rule that looks like the whole answer reaches almost none of them. It rests on four rules instead.

**1. A floor, once, in [`_focus.scss`](../../enterprise-gpt-ui/src/styles/_focus.scss).** One `:focus-visible` rule drawing `--focus-ring`, imported after Bootstrap's components — so it beats their focus styles — and before `kit` and every component stylesheet, so theirs beat it. It is a **floor, not a policy**: at `(0,1,0)` it loses to anything declared on a class, and component styles carry an `[_ngcontent]` attribute besides. What it catches is the element with **no rule at all** — `pill-subnav`'s pills, the auth screens' programmatically focused headings, an un-classed `<a>` or `<button>` added later — each of which previously fell back to a user-agent outline that differs per browser and was measured by nobody. `:focus-visible` rather than `:focus`, so a pointer press draws nothing; the six rules still on plain `:focus` each carry their reason at the site.

**2. Bootstrap's three components are overridden by name, because the floor cannot reach them.** `.btn`, `.nav-link` and `.form-check-input` each declare `outline: 0` at `(0,2,0)`, which beats a bare `:focus-visible` whatever the source order — the first cut of this work added the floor and reached none of them. What they drew instead was off-palette and well under 3:1: `--bs-btn-focus-shadow` resolves to `rgba(55,81,105,.5)` on the primary button (2.4:1 light, 1.3:1 dark), and `.nav-link` and the checkbox share Bootstrap's default `rgba(13,110,253,.25)` at 1.41:1 and 1.31:1. Between them that is every dialog footer button, every admin row action, the auth screens' calls to action, the project-detail tabs and every checkbox in the app. All three are now named in `_bootstrap-overrides.scss`, beside the `.form-control` / `.form-select` rule that already lived there. `bootstrap/scss/helpers/focus-ring` came **out** of `_bootstrap-components.scss` with them — nothing used `.focus-ring`, and what it emits is `outline: 0` plus that same 1.4:1 wash waiting for its first user.

**3. The ring is an `outline` with an offset, never a flush `box-shadow` on a filled control.** A ring drawn *on* `--btnP-bg` measures 2.89:1 in light and **1.00:1** in dark, where the button fill *is* the focus token. Offset, it sits on the page behind, which is the surface `check-tokens.mjs` measures — and is why `--btnP-bg` sits on that gate's exclusion record rather than in its pair list. The corollary is the trap: an **inset** ring on a filled control would be invisible and the gate would not catch it. The two inset rings that exist — the menu row and the project-picker row, both full-bleed with no margin for an outline to sit in — set `--surface-2` on the same rule, which is measured.

**4. The checkbox takes two rules, and their order is load-bearing.** Chrome does not match `:focus-visible` on a checkbox focused by pointer, so `.form-check-input:focus` clears Bootstrap's wash and its off-palette border, and `.form-check-input:focus-visible` **after it** draws the ring. Both are `(0,2,0)` and a keyboard-focused box matches both, so the later of the two wins — reversing them would suppress the very ring the second draws.

**`--ring` is not a focus indicator, and `check:forbidden` now says so.** It is `rgba(33,168,216,.45)`, made for the `ringpulse` keyframe to fade out from under a control; used as a ring it composites to about 1.4:1 on `--surface`. It was drawing the indicator on three composer controls, the empty-state chips and the composer card. The two form-field rules were the same defect in another colour — an 18%-alpha accent glow on `.form-control:focus`, the same in red on `.is-invalid:focus`. All now draw from `--focus-ring`, except the invalid field, which draws from `--fail` (5.30:1 on `--surface`) so it keeps its own colour **and** a compliant indicator rather than trading one for the other.

**One coupling is worth knowing before renaming a class.** The composer's textarea suppresses its own outline, so the card's `:has(.composer__prompt:focus)` ring is its only indicator — SC 1.4.11 tied to a class-name string in a selector. `composer.spec.ts` pins it, because a rename would otherwise remove that indicator silently with `check:forbidden` still green. `:focus-within` on the card now sets only the border colour, which is the board's "this composer is active" affordance rather than the focus indicator; scoping it was forced rather than tidy, because at 45% alpha a card ring under a focused button was invisible enough not to matter, and a solid one would draw a second ring around the whole composer every time a keyboard user tabbed onto any control inside it.

One residual is recorded rather than hidden: **a focused `.btn` held down briefly regains Bootstrap's wash**, because `.btn:first-child:active:focus-visible` is `(0,3,0)`. It is transient and cosmetic, and there is no persistent case — `.btn.active`, `.btn.show` and `.btn-check` appear nowhere in the app.

### 8.2 Live regions

The split is polite versus assertive, and it is structural rather than a per-toast decision:

```html
<div role="status" aria-live="polite"    …>  <!-- success, info    -->
<div role="alert"  aria-live="assertive" …>  <!-- error, warning   -->
```

**Two sibling regions, because a region cannot be both.** Success and info must not interrupt; an error must. Both have to exist in the DOM **before** the first toast arrives — a live region created at the same moment as its content is not announced — which is why `<app-toast-region />` is mounted once in the app shell and never per route.

Everywhere else in the kit, `polite` is the default and the announcement is deliberately quiet — as it is on the chat route, whose regions are owned by the feature rather than by the kit and are listed here because this table is where a reader looks for the inventory:

| Where | Region | Announces |
| --- | --- | --- |
| Data table | `aria-live="polite"`, visually hidden, and the table's `aria-describedby` | "24 results" — so a filter that narrows the list is announced without interrupting |
| Paginator | `aria-live="polite"` | "Showing 21–40 of 137 users" — so changing page announces the new range instead of leaving a screen reader to work out that the table changed underneath it |
| Bulk action bar | `aria-live="polite"`, visually hidden | "3 selected". The bar itself **must not take focus** when it appears: the user is still working through the checkboxes |
| Attachment chip | `role="status"` on the sub-status line | Ingest progress: worth hearing, not worth interrupting |
| Fatal shell | `role="alert"` + `aria-live="assertive"`, applied **after** the content is final | The one genuinely assertive case outside toasts |
| Streaming answer | `aria-live="polite"` **plus its own `aria-busy`**, on the live turn only; the settled turn that replaces it carries neither | The answer, **once**. A busy region accumulates its changes and exposes them when busy clears, which is the whole reason a polite region over streaming text does not read every delta (US-1402) |
| Turn status | `role="status"`, visually hidden, rendered by `Chat` **outside** the transcript and last in the document | "Get forecast is running", "Writing the answer", "1 more step failed" — the running commentary, outside the transcript because anything inside its `aria-busy` container is deferred along with the answer, and last because `visually-hidden` is clipped rather than hidden |
| Transcript | two sibling `role="status"` regions | Abnormal turn endings (stopped, failed, cut off) and the code-block copy confirmation — kept apart because the first empties for the length of a turn while a code block can be copied mid-turn |
| Activity cards, reasoning region | `aria-live="off"` | Nothing. They sit inside the answer's region, so releasing it would otherwise flush every card's state label, kind, source and sub-status list at the reader — a second time, since the turn status region already narrated each one as it happened |

The chat rows are written up in full, with the residual risk that the `aria-busy` release carries, in [Answer Rendering §10](answer-rendering.md#10-following-a-streaming-answer-with-a-screen-reader-us-1402).

### 8.3 Names, roles and colour

- **Every ARIA role on the data table is stated rather than implied.** `display: grid` on a `<table>` strips the implicit roles in Chrome and Firefox, and the boards draw these rows as a grid — so `role="table"`, `rowgroup`, `row`, `columnheader` and `cell` are all written out. Removing them because "the element already says that" is a regression the markup cannot show you.
- **`appTooltip` sets `aria-label`, not `aria-describedby`** — but only when the host has no name of its own, including from its visible text. The controls it labels are icon-only and have no accessible name at all, so the tooltip text *is* the name; wiring it as a description would leave the button nameless and have the text announced twice. It refuses to override a visible label, which would break WCAG SC 2.5.3 (Label in Name). It shows on focus as well as hover and is dismissed by Escape **from anywhere**, as SC 1.4.13 requires — a host-scoped listener would leave a hover-shown tooltip undismissable while focus is elsewhere, which is precisely the case that criterion exists for.
- **Colour is never the only channel** (WCAG 1.4.1). `StatusDot` requires a label and `labelHidden` moves it to the visually-hidden class rather than removing it; `SourceBadge` renders the words "Uploaded" / "Generated"; `PermissionBadge`'s padlock is decoration and the reason is spelled out, because "there is a padlock" is not a reason.
- **`appMenuItem` belongs on a `<button>` or an `<a>`** — never a `<div>` or an `<i>`.

## 9. Corrections made against the design boards

Each is a deliberate departure, authorised by the PRD's accessibility-precedence rule, and each is recorded in the component that makes it.

| Board | Kit | Why |
| --- | --- | --- |
| Row selection drawn as `<i class="bi-square">` / `bi-check-square-fill` glyphs | Real `<input type="checkbox">`, per-row `aria-label` ("Select Helios release"), plus a select-all with `indeterminate` | A glyph is not focusable, not operable by keyboard, and reports no checked state |
| Clickable `<i>` elements for row actions | `<button>` or `<a>` everywhere; `appMenuItem` documents the constraint | Same three reasons, plus no accessible name |
| Pager ends drawn as dimmed `<span>`s | Real `<button disabled>` | A dimmed span is neither focusable nor announced as unavailable |
| Pills ~31px tall at the mobile breakpoint | `min-height: 44px` on pill navigation and card-row actions | The board's own caption asks for 44 px targets at that width, so this is its intent rather than a departure from it |
| Tooltip as a decorative flyout, host unnamed | Tooltip text becomes the host's `aria-label`; Escape dismisses document-wide | §8.3 |
| No `aria-live` anywhere | Two toast regions, plus polite regions on the table, pager and selection bar — and, since US-1402, on the streaming answer and the turn status | §8.2 |
| A focused field drawn with a tinted glow, `rgba(33, 168, 216, .18)` | `--focus-ring` at 2px, with the accent kept for the border | The glow composites to about 1.2:1 on `--surface`, and Bootstrap sets `outline: 0` besides — so it is a field's *only* indicator and it fails SC 1.4.11. US-1401 replaced it, and four more like it (§8.1) |
| Tooltip flyout hard-coded `#0B1F33` on white | `--tooltip-bg` / `--tooltip-fg` tokens | `#0B1F33` *is* the dark theme's body background — the tooltip would be invisible in dark |
| Fonts, Bootstrap, icons and React from CDNs | All four self-hosted from npm | FR-51: zero third-party requests at runtime. `check:forbidden` enforces it |
| Light `--warn` `#A96A10` | `#9C6210`, **changed upstream in `theme.css`** | 4.41:1 on white, 4.21:1 on the page and 3.99:1 on its own tinted panel — all under AA for text below 18.66px, while the token is used as a `color` in eight stylesheets. This closes the PRD's §9 open question ([Accessibility audit §6.1](accessibility-audit.md#61-light---warn-a96a10-to-9c6210)) |
| Dark `--active-bg` `#173A58` | `#16354D`, **changed upstream in `theme.css`** | `--brand` on it measured **4.29:1** — the admin rail's active tab, the sidebar's active nav link and the paginator's current page. Found by US-1405's axe run, not by this table, and only once the browser viewport was pinned to a desktop width ([Accessibility audit §6.2](accessibility-audit.md#62-dark---active-bg-173a58-to-16354d)) |
| The project-detail tab strip as stacked accordions below 768px (frame `4e`) | The same `PillSubnav` the admin rail uses | The three tabs are real child routes, one behind a `canDeactivate` guard that can refuse the navigation, so `aria-expanded` on a `routerLink` would announce a state the control neither owns nor can guarantee ([Responsive layout §8.5](responsive-layout.md#85-the-one-departure-from-the-boards)) |

The last two corrections are the first changes this project has made to `docs/design/project/theme.css` itself. Both had to go upstream rather than being overridden locally: `check:tokens` enforces parity with the design bundle, so a local override fails the gate by construction.

## 10. Gates

`npm run lint` is ESLint plus three Node checks; `npm run build` adds a fourth; `npm run test:a11y` and `check:contract` are separate, the first because it needs a browser and the second because it needs the NuGet cache.

| Command | What it enforces |
| --- | --- |
| `npm run lint:es` | Flat config: ESLint 10, typescript-eslint 8.66, angular-eslint 21.4 (`tsRecommended` + `templateRecommended` + `templateAccessibility`), `@ngrx/eslint-plugin` 21.1.1 at `signalsTypeChecked`, and `no-restricted-syntax` banning `bypassSecurityTrustHtml` in three syntactic forms |
| `npm run check:icons` | Every `bi-*` reference under `src/` is in the sprite manifest |
| `npm run check:forbidden` | Text backstop over **nine** patterns: `bypassSecurityTrustHtml`, a Google Fonts origin, a CDN origin, one of Prism's shipped stylesheets, any `localStorage.setItem` outside `core/storage/local-preferences.ts` (US-205), US-1401's suppressed focus indicator (`outline: none`/`0`, every spelling, both longhands included) and `var(--ring)` outside its keyframe, and — since P7 — **a hardcoded application breakpoint** (US-1403) and **a fifth keyframe** (US-1404). Comment-stripped, so prose is fine |
| `npm run check:tokens` | Tokens versus `theme.css`, `index.html`'s duplicated colours versus the tokens, the theme storage key in both places (now `PREFERENCE_KEYS.theme`), **the four keyframe bodies** and **the two breakpoints** against their second copies, and **46** measured contrast pairs — 26 code-surface ones from US-604, US-1401's twelve `--focus-ring` and `--fail` pairs, and US-1405's eight: `--warn` on `surface`, `bs-body-bg` and `warn-bg`, and `--brand` on `active-bg`, in both themes |
| `npm run test:a11y` | axe-core over all seven routes in a real headless Chromium, in both themes, plus the shell at three widths — failing on any serious or critical violation, and on horizontal overflow ([Accessibility audit](accessibility-audit.md)) |
| `npm run check:contract` | The vendored streaming contract, byte-for-byte against the NuGet package (see [the README's "Vendored contract"](../../enterprise-gpt-ui/README.md#vendored-contract)) |
| `npm run build` | The budgets — **675 kB warn / 720 kB error** initial, **66 / 80 kB** on `styles` since US-1405 — then `check-initial-chunk.mjs` over the esbuild metafile |

Eleven points about that set that will otherwise look arbitrary:

- **`signalsTypeChecked`, not `signals`.** The plain preset omits `with-state-no-arrays-at-root-level`, and that rule calls `getParserServices()` unconditionally, so it throws without type information. `projectService: true` is therefore mandatory rather than an optimization. The four NgRx rules it brings — no arrays at the root of `withState`, `patchState`-only writes, no `store.method()` inside a `computed`, and the `with*` naming convention — are the ones the store features were written against.
- **`@typescript-eslint/no-unused-vars` is off, on purpose.** `tsconfig.json`'s `noUnusedLocals` / `noUnusedParameters` own that class of mistake: they run on every build and every test rather than only under `npm run lint`, and unlike the ESLint rule they count a `{@link}` in a doc comment as a use.
- **`check-initial-chunk.mjs` guards identity, not size.** A `bundle`-type budget names a chunk only from `initialFiles` or an `entryPoint`, and a chunk produced by `import('mermaid')` has neither — so the budget sums zero bytes and can never fail. This script walks static imports from `src/main.ts` and fails if mermaid or katex ever enter that graph, naming the library and the input path that put it there.
- **`check:forbidden` exists even though ESLint carries the same rule structurally.** ESLint cannot see a property name assembled at run time, and it cannot see an inline `// eslint-disable-next-line`. A text scan sees both. Running it as a Node script rather than a CI `grep` step means Windows developers get the identical gate locally.
- **The focus patterns are `escapable`; the other seven are not, and that distinction was a review finding.** A per-line `// focus-check-ignore-next-line` opts one line out, the same shape `check-icon-names.mjs` uses, and deliberately per-line rather than per-file: an exemption naming its reason at the site it applies to is reviewable, and one listed in the script is not. Four sites carry it — the two inset menu rings, the composer's textarea and the checkbox's wash-killer. An earlier cut applied the hatch to **all** rules, which would have let a comment wave through `bypassSecurityTrustHtml` and defeat the backstop this script exists to be; that was caught in review and the fix verified by planting such a call.
- **`check:tokens` guards its own record, in both directions.** The pair list is hand-written, and the way it rots is a *new* surface: a ring drawn on it would be measured by nothing while every gate stayed green. So the surfaces are read out of the stylesheets rather than guessed from token names — a name heuristic looked tidier and was wrong, since `--brand` and `--accent` are both painted under focusable content and neither ends in `bg`. Every token any `.scss` under `src/` paints as a background must be measured against `--focus-ring` or excluded with a reason, and every exclusion must still correspond to something painted. The scan follows **variable indirection** (`--anything-bg: var(--x)`), because that is how every Bootstrap fill is set; matching only the direct form missed the hovered primary button, and missed it silently. Twelve tokens are on the exclusion record, each reason verified rather than guessed, and the recurring one is §8.1's third rule — an offset ring is not on the control it belongs to.
- **The `outline` scan reads `src/` only, not the compiled stylesheet — which is exactly how Bootstrap's five `outline: 0` declarations passed the gate written to catch them.** Running it over `dist/`'s `styles.css` would close the hole, but that needs a build step and `check:forbidden` runs inside `npm run lint` with none. Recorded as a follow-up in the [build order](../prd/enterprise-ui-rebuild-build-order.md), not as a solved problem.
- **Two of the gates now check that a value stated twice stayed the same, because two of them have to be.** Sass cannot read a TypeScript constant and `matchMedia` cannot read a Sass variable, so the application's two breakpoints exist in `_breakpoints.scss` and `breakpoints.ts` both; `check:tokens` compares them, including that **every maximum is exactly 0.02px below a declared minimum** — a `767px` maximum beside a `768px` minimum looks tidier and leaves a 0.98px band matching no mode at all. The keyframe parity check is the same shape against `theme.css`: the four bodies are compared with whitespace and the optional trailing semicolon removed, because `_motion.scss` is Prettier-formatted and `theme.css` is minified, so those are the only differences that are _not_ a change of behaviour. Both scans read the files **comment-stripped**, since both breakpoint files document the fractional-maximum bug by quoting the wrong queries and would otherwise fail the gate on their own explanation.
- **The `--warn` and `--active-bg` rows are the first pairs whose fix went upstream into the design bundle** (§9). `check:tokens` enforces parity with `theme.css`, so a contrast failure in a token transcribed verbatim is not fixable in the app at all — which is a feature: it forces the correction to the place every future consumer of the boards will read.
- **`under:` is what makes a translucent surface measurable.** `--warn-bg` is an opaque hex in light and an `rgba` in dark, and one surface the reader sees as one colour was being reported as unmeasurable in half the themes. `under:` names what it is painted on so both halves composite and both get measured, rather than the dark half being quietly skipped.
- **The `localStorage` pattern is a *policy* gate, not a safety one, and it is the enforcement point for a criterion.** US-205 requires that after sign-out the browser retains the theme and sidebar preferences and nothing else. Sign-out deliberately sweeps nothing — a runtime cleanup would make the rule look optional and would eventually delete a key some story meant to keep — so the guarantee comes from there being exactly one call site with an enumerated key list. Three files are exempt: the module itself, its spec, and `user-footer.spec.ts`, which plants a disallowed key on purpose to prove sign-out does *not* remove it.

`.github/workflows/ui-ci.yml` runs format, lint, test, build and — since US-1405 — a Chromium install and the accessibility audit, all on one job (`Lint, test, build`), and the contract check on a second (`Andes contract drift`) — isolated because it is the only thing here that needs the .NET toolchain, and roughly 60 s of `dotnet restore` does not belong on the critical path of every UI pull request. A third job, `Detect UI changes`, decides whether either needs to run: the workflow carries no `paths:` filter, because a workflow skipped by one never reports and so can never be a required check. See [Pull-request checks](../ci/pull-request-checks.md).

## 11. The gallery

```bash
npm start
# then open http://localhost:4200/ui-kit
```

Every kit component, light and dark **side by side**, because `data-bs-theme` is per-subtree. It exists because the kit has no consumer until EP-3 and two of its properties cannot be checked by a unit test: that a component reads correctly in dark as well as light, and that motion stops under `prefers-reduced-motion`. US-1405's browser target now covers the first of those two automatically for every route (§10); the second is still eyes-only, because a suppressed animation looks identical to an animation that never started.

US-1210 added the two newest entries: the brand marks (§5.4), sized larger than the monochrome glyphs above them because these are logos and the point of showing them here is that they must read against both surfaces without inheriting either one's text colour; and the switch (§7.8), shown inside the one button shape it is ever used in — a bare `<app-switch>` on this page would model a control that does not exist, since the component itself carries no role and no state of its own.

US-204 added a third kind of entry, and it is the more interesting use of the gallery: a **drop zone with a checkbox on the grant**, standing in for `SessionStore.canUploadFiles()`, which no development session can toggle. Tick it off and drag a file over the box — nothing lights up, the cursor reads "no drop", and a file released on the page background is swallowed rather than navigating away. That is the half of US-204 only a real pointer can check.

The route is guarded with `canMatch: [() => isDevMode()]`, so the chunk is **never requested in production** — the same mechanism US-203 uses to keep the admin chunk off a non-administrator's device. It is scaffolding; delete it once the real screens exercise the kit.

## 12. Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| An icon renders as an empty box | The name is not in the sprite. Either it is missing from `icon-names.ts`, or it is there but `npm run assets:icons` has not run since. In development the console names it |
| Every icon renders black in both themes | The sprite is being referenced externally instead of injected. `currentColor` resolves against the sprite's own document (§5.1) |
| No icons at all | `loadIconSprite()` swallowed a failure by design. Check the network panel for `icons/sprite.svg`; the app is meant to keep working without it |
| A brand mark renders as an empty box | The name is not in `brand-icon-names.ts`, or it is there with no matching `brand/<name>.svg` source — the sprite build fails loudly for the latter, so check the console first for a dev-mode name the manifest does not recognise (§5.4) |
| A brand mark renders tinted to the surrounding text colour instead of its own colours | `<app-icon>` was used instead of `<app-brand-icon>`. `icon.scss` forces `fill: currentColor`, which only the `bi-*` lane of the sprite is built to take (§5.4) |
| A dark-theme user sees a light flash | Check `optimization.styles.inlineCritical` is still `false` in `angular.json` (§3.2). It is the only setting that can reintroduce this |
| `npm run check:tokens` fails after a design change | `theme.css` moved and the app did not. Update the map in `_tokens.scss`; if the changed value is `--bs-body-bg` or `--bs-body-color`, update the duplicated pair in `index.html` too |
| Sass `@error: Token --x is defined in $light but missing from $dark` | Exactly what it says. Add the token to both maps |
| A component's styles have no effect and the bundle grew | The component `@use`d `tokens` or `typography`. Remove the import and read `var(--…)` (§1) |
| A table column renders blank | The `appTableCell` key does not match `TableColumn.key`, or the template is inside a wrapper — `descendants: true` covers most of those, a mismatched key covers the rest |
| A `text`-only column renders blank on a card but not on the table | The card branch's `cellText` fallback came off. It is the same fallback the table branch has always had, repeated per slot rather than shared ([Administration §3.3.1](administration.md#331-six-defects-in-the-card-anatomy-and-four-bugs-found-while-fixing-them)) |
| A checkbox looks dead: clicking changes nothing | A selection updater mutated the `Set` and wrote back the same reference, so `patchState` performed no write (§7.4) |
| A drop zone highlights and then does nothing when the file is released | `preventDefault()` is missing on `dragover`. Without it the element never becomes a drop target and `drop` never fires — the single most common cause of a zone that looks right |
| The drop overlay flickers off while the pointer is still inside the zone | The drag state was reduced to a boolean. `dragleave` fires on every crossing into a child, which is why `FileDropTarget` counts depth (§7.7) |
| `npm run lint` fails naming "a suppressed focus indicator" | The rule removed the ring and put nothing back. Draw one — an inset `box-shadow` for a full-bleed row — then add `// focus-check-ignore-next-line` above the suppression saying why (§10). Do not exempt the file |
| `npm run lint` fails naming "the pulse colour used outside its keyframe" | `--ring` is `ringpulse`'s 45%-alpha colour and measures about 1.4:1. The focus indicator is `--focus-ring` (§8.1) |
| `check:tokens` fails with "is painted as a background … but CONTRAST_PAIRS does not measure `--focus-ring` against it" | A stylesheet started painting a new surface, directly or through a `--…-bg` alias. Either measure the ring on it, or record why not in `FOCUS_SURFACE_EXCLUSIONS` — the gate is refusing to fall silently behind the stylesheets (§10) |
| `check:tokens` fails with "nothing paints it as a background any more" | The mirror of the row above: an exclusion outlived its surface, and a stale one reads as a considered decision about a live one. Delete the entry |
| A focused control draws two rings, one around it and one around the whole composer | The card's ring went back to `:focus-within`. It is scoped to `:has(.composer__prompt:focus)` on purpose (§8.1) |
| `npm run lint` fails naming a `localStorage` write | Route it through `core/storage/local-preferences.ts` and add the key to `PREFERENCE_KEYS` — after checking against §10's rule that the value is about the browser, not about the user |
| `format:check` fails on files nobody touched | Git for Windows checked the tree out as CRLF. `enterprise-gpt-ui/.gitattributes` pins `eol=lf`; delete the files and check them out again |
| `npm run lint` fails naming "a hardcoded application breakpoint" | A media query spelled 767, 767.98, 768, 1023.98 or 1024 directly. Use a `bp.*` mixin, or `MOBILE_VIEWPORT` / `TABLET_VIEWPORT` in TypeScript ([Responsive layout §3](responsive-layout.md#3-one-breakpoint-record-two-copies-two-gates)) |
| `npm run lint` fails naming "a fifth keyframe" | The app defines exactly four, all transcribed from `theme.css`. Whatever has to move uses a `transition` on the `--t-fast` / `--t-slow` scale, which the reduced-motion block already suppresses (§3.4) |
| `check:tokens` fails with "@keyframes … has drifted from theme.css" | The transcription and the design bundle disagree. Whichever is wrong, they must match — this is the check that stops a drifted copy changing how the app moves with every other gate green |
| `check:tokens` fails with "carries a maximum of 767px, which is not 0.02px below any declared minimum" | A whole-number maximum was written beside a whole-number minimum, reopening the fractional dead zone (§10) |
| `check:tokens` reports a background as unmeasurable in one theme only | It is translucent there. Give the pair an `under:` naming the surface beneath it, so both themes composite and both get measured (§10) |
| `npm run test:a11y` fails on a colour rule and `npm run lint` is green | The two measure different things: `check:tokens` reads the token table, axe reads what the browser painted. Fix the pair, then add it to `CONTRAST_PAIRS` so the table catches it next time ([Accessibility audit §6](accessibility-audit.md#6-the-two-failures-the-run-found)) |
| `npm run build` fails naming "the axe auditor" | Application code imported `axe-core`, or imported `src/testing/a11y.ts`. Only `*.a11y.spec.ts` may |

## 13. Key files

| Concern | File |
| --- | --- |
| Tokens | [`src/styles/_tokens.scss`](../../enterprise-gpt-ui/src/styles/_tokens.scss) |
| Stylesheet order | [`src/styles.scss`](../../enterprise-gpt-ui/src/styles.scss) |
| Bootstrap trim | [`_bootstrap-config.scss`](../../enterprise-gpt-ui/src/styles/_bootstrap-config.scss), [`_bootstrap-components.scss`](../../enterprise-gpt-ui/src/styles/_bootstrap-components.scss) |
| Type | [`_typography.scss`](../../enterprise-gpt-ui/src/styles/_typography.scss), [`scripts/copy-fonts.mjs`](../../enterprise-gpt-ui/scripts/copy-fonts.mjs) |
| Motion | [`_motion.scss`](../../enterprise-gpt-ui/src/styles/_motion.scss), [`motion.spec.ts`](../../enterprise-gpt-ui/src/styles/motion.spec.ts) |
| Breakpoints | [`_breakpoints.scss`](../../enterprise-gpt-ui/src/styles/_breakpoints.scss), [`shared/layout/breakpoints.ts`](../../enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts) — see [Responsive layout](responsive-layout.md) |
| Safe areas | [`_kit.scss`](../../enterprise-gpt-ui/src/styles/_kit.scss), [`src/index.html`](../../enterprise-gpt-ui/src/index.html) |
| The axe harness | [`src/testing/a11y.ts`](../../enterprise-gpt-ui/src/testing/a11y.ts) — see [Accessibility audit](accessibility-audit.md) |
| Focus floor and the Bootstrap overrides | [`_focus.scss`](../../enterprise-gpt-ui/src/styles/_focus.scss), [`_bootstrap-overrides.scss`](../../enterprise-gpt-ui/src/styles/_bootstrap-overrides.scss) |
| Pre-paint script, preloads, shell | [`src/index.html`](../../enterprise-gpt-ui/src/index.html) |
| Theme state | [`core/theme/theme-service.ts`](../../enterprise-gpt-ui/src/app/core/theme/theme-service.ts) |
| Stored preferences and the `localStorage` allowlist | [`core/storage/local-preferences.ts`](../../enterprise-gpt-ui/src/app/core/storage/local-preferences.ts) |
| Icon manifest / sprite / injection | [`shared/icon/icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/icon-names.ts), [`scripts/build-icon-sprite.mjs`](../../enterprise-gpt-ui/scripts/build-icon-sprite.mjs), [`core/icons/load-icon-sprite.ts`](../../enterprise-gpt-ui/src/app/core/icons/load-icon-sprite.ts) |
| Brand mark manifest / sources / component | [`shared/icon/brand-icon-names.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon-names.ts), [`shared/icon/brand/`](../../enterprise-gpt-ui/src/app/shared/icon/brand/), [`shared/icon/brand-icon.ts`](../../enterprise-gpt-ui/src/app/shared/icon/brand-icon.ts) |
| Switch | [`shared/switch/switch.ts`](../../enterprise-gpt-ui/src/app/shared/switch/switch.ts) |
| Brand images | [`scripts/build-brand-images.mjs`](../../enterprise-gpt-ui/scripts/build-brand-images.mjs), [`shared/brand-logo/`](../../enterprise-gpt-ui/src/app/shared/brand-logo/) |
| Accessibility primitives | [`shared/a11y/`](../../enterprise-gpt-ui/src/app/shared/a11y/) — including `focus-on-render.ts`, which US-205 moved here from `features/auth/` |
| Upload drop zone and overlay | [`shared/upload/`](../../enterprise-gpt-ui/src/app/shared/upload/), [`core/dnd/prevent-drop-navigation.ts`](../../enterprise-gpt-ui/src/app/core/dnd/prevent-drop-navigation.ts) |
| User footer and sign-out interstitial | [`shared/nav/user-footer/`](../../enterprise-gpt-ui/src/app/shared/nav/user-footer/), [`shared/feedback/signing-out/`](../../enterprise-gpt-ui/src/app/shared/feedback/signing-out/) |
| Toast state | [`core/notifications/toast-store.ts`](../../enterprise-gpt-ui/src/app/core/notifications/toast-store.ts) |
| Lint and checks | [`eslint.config.mjs`](../../enterprise-gpt-ui/eslint.config.mjs), [`scripts/`](../../enterprise-gpt-ui/scripts/) |
| CI | [`.github/workflows/ui-ci.yml`](../../.github/workflows/ui-ci.yml) |
| Related reference | [Frontend Foundation](frontend-foundation.md), [Authentication and Session](authentication-and-session.md), [Shell and Navigation](shell-and-navigation.md), [Responsive layout](responsive-layout.md), [Accessibility audit](accessibility-audit.md), [Enterprise UI Rebuild PRD](../prd/enterprise-ui-rebuild.md), [`enterprise-gpt-ui/README.md`](../../enterprise-gpt-ui/README.md) |
