# Design System

Tokens, theming, typography, icons, and the accessibility contracts the shared component kit
guarantees.

## Tokens

`src/styles/_tokens.scss` is the **source of record** for every colour, as two Sass maps (`$light`,
`$dark`) emitted as `:root` custom properties.

Property names keep their original casing — `--btnP-bg` is unusual, but custom properties are
case-sensitive.

**Never `@use` this file from a component stylesheet.** It emits `:root` blocks, and under emulated
encapsulation a component would re-emit them scoped to its own attribute — matching nothing and
costing bytes in every component. Components read tokens with `var(--...)` and import nothing.

`scripts/check-tokens.mjs` gates three things: the colours duplicated into `index.html` stay in step
with the tokens, the code-surface and focus-ring pairs clear their WCAG minimum in both themes, and
the two copies of the breakpoint table agree.

## Theming and the pre-paint contract

```text
index.html <head>
  inline <style>    two colour pairs, duplicated from the tokens
  inline <script>   reads localStorage['egpt.theme'], falls back to matchMedia,
                    sets <html data-bs-theme> and <html>.style.colorScheme
                            -> first paint happens here, already correct
styles.scss         tokens resolve against an attribute that is already set
provideAppInitializer(() => inject(ThemeService))
                    reads the same key, applies the same values, takes over
```

The handover changes nothing on screen, which is the goal: the script and the service must agree,
and `check:tokens` asserts the two things they share — the storage key and the shell colours.

## Typography

Three faces, twelve `woff2` files, all self-hosted.

| Family | Weights | Subsets | Renders |
| --- | --- | --- | --- |
| Inter (`$font-sans`) | 400, 500, 600 | latin, latin-ext | Body and UI, including display names and model output — which is why it is the one family carrying latin-ext |
| Montserrat (`$font-display`) | 600, 700, 800 | latin | Headings, KPI numerals, brand titles |
| JetBrains Mono (`$font-mono`) | 400, 500, 600 | latin | Token counts, durations, ids, deployment names, trace ids |

`npm run assets:fonts` copies them out of `@fontsource` into the gitignored `public/fonts`. A script
rather than an `angular.json` asset glob, because a glob that stops matching after an upstream
rename ships zero fonts *silently*; the script names all twelve files and fails loudly.

Points that otherwise cost an hour:

- **`unicode-range` is copied from `@fontsource`'s own per-subset CSS**, so the ranges match the
  files byte for byte. A character outside every listed range falls back to the next family rather
  than downloading a face that cannot render it — which is what makes the latin-ext files free until
  an accented character appears.
- **`font-display: swap`, not `optional`.** A missing glyph for a fraction of a second beats an
  invisible one, and the two critical faces are preloaded.
- **`index.html` preloads Inter 400 and Montserrat 600 only.** `crossorigin` is required on those
  links **even same-origin** — fonts are always fetched in CORS mode, and omitting it makes the
  browser issue a second, unused request.
- **`$font-path` is root-relative (`/fonts`).** The build resolves every non-root `url()` as a module
  and fails on one it cannot find. A sub-path deployment needs `--base-href` and `deployUrl`.
- **The partial is `_typography`, not `_type`.** `src/styles` is on the Sass load path, so
  `@import 'type'` would silently shadow `bootstrap/scss/type`.

## Icons

The icon font is **not** shipped: its stylesheet alone is roughly 72 kB and would carry 2,078 glyphs
to render the ~75 in use.

```text
shared/icon/icon-names.ts    sorted names + the IconName union     (checked in)
   |  npm run assets:icons
public/icons/sprite.svg      one <symbol> per glyph, fill=currentColor   (gitignored)
   |  loadIconSprite(), in parallel with the config fetch in main.ts
<div aria-hidden> prepended to <body>  ->  <use href="#bi-search"> resolves same-document
```

**The sprite is injected, not referenced.** An externally referenced sprite is a separate document,
and `currentColor` inside it resolves against *that* document's initial colour — black — instead of
inheriting from the host. Every icon would render black in both themes.

It is an asset rather than a bundled string: inlining it would put roughly 34 kB into the initial JS
chunk where it counts against the budget. In `public/` it costs nothing and is fetched concurrently
with the `config.json` fetch that already gates bootstrap, so there is no icon pop-in.

`loadIconSprite()` never rejects — an icon-less app is degraded, not broken. It refuses markup that
does not start with `<svg>`, because a host with SPA fallback routing answers a missing file with
`index.html` and a `200`, and injecting that would put a second `<app-root>` into the page.

`npm run check:icons` asserts every `bi-*` name referenced anywhere under `src/` exists in the
manifest — covering what `strictTemplates` cannot see, such as a raw `class="bi bi-..."` in a
template.

## Accessibility contracts

### Focus

- **In.** Opening a modal or offcanvas focuses `[data-modal-autofocus]`, else the first tabbable
  element, else the heading. Opening a menu with ArrowDown focuses the first item; ArrowUp, the last.
- **Within.** A `showModal()` dialog gets real inertness from the top layer. The menu roves focus
  with Arrow / Home / End and gives Tab back to the page.
- **Out.** `captureFocusOrigin` records the invoker before the overlay opens and restores it on close
  **and on destroy**. The destroy path is not belt-and-braces: `dialog.close()` queues its event, so
  a modal closed by a route change would have its `(close)` binding torn down before the event fired
  and focus would drop to `<body>`. The test shim queues the event as the platform does, so deleting
  the restore fails a spec rather than leaving every spec green.
- **Visible.** `--focus-ring` at 2px with a 2px **offset**, measured against every surface by
  `check:tokens` rather than claimed by a comment.

Four rules make the visible ring actually reach things:

1. **A floor, once, in `_focus.scss`.** One `:focus-visible` rule imported after Bootstrap's
   components so it beats their focus styles, and before `kit` and every component stylesheet so
   theirs beat it. It is a floor, not a policy: at `(0,1,0)` it loses to anything declared on a
   class. What it catches is the element with **no rule at all**.
2. **Bootstrap's three components are overridden by name, because the floor cannot reach them.**
   `.btn`, `.nav-link` and `.form-check-input` each declare `outline: 0` at `(0,2,0)`, which beats a
   bare `:focus-visible` whatever the source order. What they drew instead was off-palette and well
   under 3:1.
3. **The ring is an `outline` with an offset, never a flush `box-shadow` on a filled control.** A
   ring drawn *on* a filled button measures 1.00:1 in dark, where the button fill *is* the focus
   token. Offset, it sits on the page behind, which is the surface the gate measures. The corollary
   is the trap: an **inset** ring on a filled control would be invisible and the gate would not catch
   it — the two inset rings that exist set a measured surface on the same rule.
4. **The checkbox takes two rules, and their order is load-bearing.** Chrome does not match
   `:focus-visible` on a checkbox focused by pointer, so `:focus` clears Bootstrap's wash and
   `:focus-visible` **after it** draws the ring. Both are `(0,2,0)`, so the later wins; reversing
   them would suppress the very ring the second draws.

**`--ring` is not a focus indicator**, and `check:forbidden` says so. It exists for the `ringpulse`
keyframe to fade out from under a control; used as a ring it composites to about 1.4:1.

One coupling to know before renaming a class: the composer's textarea suppresses its own outline, so
the card's `:has(.composer__prompt:focus)` ring is its only indicator — an accessibility criterion
tied to a class name in a selector. A spec pins it, because a rename would otherwise remove the
indicator silently with `check:forbidden` still green.

## Motion

Exactly four keyframes: `blink`, `spin`, `ringpulse`, `ridgedash`. `check:forbidden` refuses a
fifth, which is why Bootstrap's `_spinners.scss` and `_placeholders.scss` are excluded from the
stylesheet — each would add one. Anything else that has to move uses a transition on the
`--t-fast` / `--t-slow` scale, which `_motion.scss`'s reduced-motion block already suppresses.

`_motion.scss` is imported **last** so that block wins over everything above it.

## The kit

`shared/` holds the presentational components: avatars, badges, cards, chips, the composer, data
table and paginator, empty and error states, the icon component, overlay primitives (anchored panel,
menu, modal, offcanvas, tooltip), the search input, the ridgeline, the theme toggle and the upload
drop target. A dev-only `/ui-kit` route renders the gallery.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/styles/_tokens.scss` | The token maps |
| `enterprise-gpt-ui/src/styles/_focus.scss` | The focus floor |
| `enterprise-gpt-ui/src/styles/_bootstrap-overrides.scss` | The three components the floor cannot reach |
| `enterprise-gpt-ui/src/styles/_motion.scss` | The four keyframes and the reduced-motion block |
| `enterprise-gpt-ui/src/app/shared/icon/icon-names.ts` | The sprite manifest |
| `enterprise-gpt-ui/scripts/check-tokens.mjs` | Shell parity, contrast, breakpoint parity |
| `enterprise-gpt-ui/src/styles/motion.spec.ts` | No animation names a keyframe that does not exist |

## Related

- [layout-and-navigation.md](layout-and-navigation.md)
- [../development/testing-and-ci.md](../development/testing-and-ci.md)
