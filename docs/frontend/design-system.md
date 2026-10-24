# Design System

Tokens, theming, typography, icons, and the accessibility contracts the shared component kit
guarantees.

## Tokens

`src/styles/_tokens.scss` is the **source of record** for every colour, and it is **data only**: two
Sass maps, `$tokens-light` and `$tokens-dark`, that emit nothing. Two files consume them:

- `_bootstrap-config.scss` feeds the palette into Bootstrap's own Sass variables, so its components
  compile against the app's colours.
- `_theme.scss` emits the maps as custom properties under the same selectors as Bootstrap's `root`
  (`:root, [data-bs-theme='light']` and `[data-bs-theme='dark']`), later in the output so it wins. It
  has to: Bootstrap's dark root omits the theme colours and their `-rgb` channels, so without it
  `.text-primary` would keep its light value in dark. It also emits the `-rgb` companions and a
  `--{c}-hover` / `--{c}-active` pair per theme colour, using Bootstrap's own shade and tint amounts.

Colours carry **Bootstrap's names** wherever Bootstrap has one: `--bs-body-bg`, `--bs-body-color`,
`--bs-emphasis-color`, `--bs-secondary-color`, `--bs-secondary-bg`, `--bs-tertiary-bg`,
`--bs-border-color`, `--bs-link-*`, `--bs-primary` through `--bs-danger`, and the `warning` and
`danger` subtle and emphasis set. Each theme colour also has a `--{c}-fg`, the label colour on a
filled control. What remains app-only: `--surface`, `--think-bg`, `--bubble` and `--bubble-fg`,
`--ring`, `--focus-ring`, `--tooltip-bg` and `--tooltip-fg`, `--code-*`, `--provider-*`, and
`--show-light` / `--show-dark`.

Property names keep their original casing, because custom properties are case-sensitive.

An older name does not come back. `check:forbidden` bans each of these, so a stale `var(--muted)`
fails lint rather than resolving to nothing:

| Removed | Use |
| --- | --- |
| `--muted` | `--bs-secondary-color` |
| `--surface-2` | `--bs-tertiary-bg` |
| `--active-bg` | `--bs-secondary-bg` |
| `--brand`, `--btnP-bg` | `--bs-primary` |
| `--btnP-fg` | `--primary-fg` |
| `--accent` | `--bs-info` |
| `--ok` | `--bs-success` |
| `--warn`, `--warn-bg`, `--warn-border` | `--bs-warning`, `--bs-warning-bg-subtle`, `--bs-warning-border-subtle` |
| `--fail` | `--bs-danger` |

**How the palette reaches Bootstrap.** A Sass variable holds a `var(--...)` only where Bootstrap merely
interpolates it. Anything Bootstrap passes to `shade-color`, `tint-color`, `color-contrast`, `to-rgb`
or an SVG data URI stays a Sass colour read from the light map (or the dark map, for the `-dark`
variables). Component surfaces and states that Bootstrap only interpolates are `var()`, so they flip
with the theme: card, modal, offcanvas, dropdown, list-group and toast backgrounds are `var(--surface)`;
active states are `--bs-secondary-bg` with `--bs-primary` text; the tooltip reads `--tooltip-*`.
The dark subtle and emphasis colours are derived from the **dark** palette, not Bootstrap's.

**Never `@use` `tokens` or `theme` from a component stylesheet.** `_theme.scss` emits `:root` blocks,
and under emulated encapsulation a component would re-emit them scoped to its own attribute,
matching nothing and costing bytes in every component. Components read tokens with `var(--...)`.
`@use 'breakpoints' as bp` is fine: it emits nothing.

`scripts/check-tokens.mjs` gates:

- the colours duplicated into `index.html` stay in step with the tokens;
- the text, code-surface, tooltip and focus-ring pairs (98 in all) clear their WCAG minimum in both
  themes, including a generated `{c}-fg` on `bs-{c}` pair at 4.5:1 for every theme colour;
- `--bs-primary` flips between themes, the property the filled-control pairs depend on;
- the two copies of the breakpoint table agree.

The painted-surface scan counts every Sass `$...-bg: var(--...)` declaration **and** every `bg-*` or
`text-bg-*` utility class in a template or `.ts` file, so a new surface cannot dodge the focus-ring
measurement. A class assembled at run time is invisible to it: write the map out in full.

## Theming and the pre-paint contract

```text
index.html <head>
  inline <style>    two colour pairs, duplicated from the tokens
  inline <script>   reads localStorage['egpt.theme'], falls back to matchMedia,
                    sets <html data-bs-theme> and <html>.style.colorScheme
                            -> first paint happens here, already correct
styles.scss         tokens resolve against an attribute that is already set
provideAppInitializer(() => inject(Theme))
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
shared/icons/icon-names.ts         sorted names + the IconName union     (checked in)
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
   components so it beats their focus styles, and before `bootstrap-overrides`, `kit` and every
   component stylesheet so theirs beat it. It is a floor, not a policy: at `(0,1,0)` it loses to
   anything declared on a class. What it catches is the element with **no rule at all**.
2. **Bootstrap's focus styles are overridden by name, because the floor cannot reach them.**
   Config sets every Bootstrap focus variable to the measured 2px `--focus-ring` or to `none` (not
   `null`, which `!default` would overwrite). The rest is in `_bootstrap-overrides.scss`, in two
   groups:
   - **Offset outline** (`2px solid var(--focus-ring)`, offset 2px): `.btn`, `.btn-check + .btn`,
     `.nav-link`, `.page-link`, `.form-check-input`, `.btn-close`, `.navbar-toggler`, the carousel
     controls and `.form-control-plaintext`.
   - **Inset** (offset -2px): `.dropdown-item`, `.list-group-item-action` and `.accordion-button`.
     These are full-width rows, where an offset ring would overlap the neighbours. `.focus-inset`
     in the kit does the same for any other control inside a scroll container.
3. **The ring is an `outline` with an offset, never a flush `box-shadow` on a filled control.** A
   ring drawn *on* a filled button measures 1.00:1 in dark, where the button fill *is* the focus
   token. Offset, it sits on the page behind, which is the surface the gate measures. The
   corollary is the trap: an **inset** ring on a filled control would be invisible and the gate would
   not catch it. The inset rows above are safe because each sets a measured surface on the same rule.
   Bootstrap's `.focus-ring` helper is compiled and tuned to the measured ring (2px, opacity 1), but
   it draws flush: **never put it on a filled control.** Its colour classes are removed from the
   utilities API, because at opacity 1 they would paint unmeasured colours.
4. **The checkbox takes two rules, and their order is load-bearing.** Chrome does not match
   `:focus-visible` on a checkbox focused by pointer, so `:focus` clears Bootstrap's wash and
   `:focus-visible` **after it** draws the ring. Both are `(0,2,0)`, so the later wins; reversing
   them would suppress the very ring the second draws.

`Tooltip`, the icon-button flyout in the kit below, gates its own focus trigger on this same
predicate, so the tip appears exactly when the ring is drawn and stays silent for a focus the ring
does not mark — a pointer click, or a programmatic `.focus()` restoring a control the pointer never
entered.

**`--ring` is not a focus indicator**, and `check:forbidden` says so. It exists for the `ringpulse`
keyframe to fade out from under a control; used as a ring it composites to about 1.4:1.

One coupling to know before renaming a class: the composer's textarea suppresses its own outline, so
the card's `:has(.composer__prompt:focus)` ring is its only indicator — an accessibility criterion
tied to a class name in a selector. A spec pins it, because a rename would otherwise remove the
indicator silently with `check:forbidden` still green.

## Colour that flips

Bootstrap compiles its button, `.text-bg-*`, link-hover and table variants as static colours. A loop
over `$flipping-colors` in `_bootstrap-overrides.scss` re-points `.btn-*`, `.btn-outline-*`,
`.text-bg-*`, `.link-*` hover and `.table-*` at the per-theme tokens, so they flip. Four decisions
there are contrast fixes, not taste:

- **The label colour is the token, not white.** `--{c}-fg` is dark ink on every dark-theme fill, and
  in light for success and info. White text on a dark-theme `.btn-danger` measured 3.33:1.
- **`.btn-outline-danger` text is `--bs-danger-text-emphasis`.** The base `--bs-danger` is 4.39:1 as
  text on the dark surface.
- **`.btn-outline-secondary` is a neutral app variant**: body text, a border-colour border, and the
  tertiary and secondary backgrounds on hover and active.
- **Checked checkbox, radio, indeterminate and switch glyphs are rebuilt in dark.** Bootstrap's white
  glyph on the dark `--bs-primary` measured 2.74:1.

`text-info`, `text-success`, `link-info`, `link-success`, `btn-outline-info`, `btn-outline-success`
and `border-info` fail contrast in light (info 2.74:1, success 3.49:1 on white), so `check:forbidden`
bans them. Use the `-emphasis` or `-subtle` variants.

## Motion

The app owns exactly three keyframes: `blink`, `ringpulse` and `ridgedash`. `check:forbidden`
refuses a fourth. Bootstrap's own keyframes (`spinner-border`, `spinner-grow`, `placeholder-glow`,
`placeholder-wave`, `progress-bar-stripes`) compile from `node_modules`, outside that scan; spinners
are `.spinner-border`, never a hand-rolled rotation. Anything else that has to move uses a
transition on the `--t-fast` / `--t-slow` scale, which `_motion.scss`'s reduced-motion block already
suppresses.

`_motion.scss` is imported **last** so that block wins over everything above it.

## The kit

**Bootstrap first.** Templates use Bootstrap's components and utilities. The kit adds a class only
where Bootstrap has no equivalent, and a component stylesheet keeps only rules that are genuinely
bespoke. Bootstrap's default spacing, type and radius scales apply: there are no app-specific sizes.

Global classes in `_kit.scss`, imported after the overrides so a modifier beats the Bootstrap rule it
adjusts:

| Class | Purpose |
| --- | --- |
| `.btn-ghost` | Transparent button; secondary text, tertiary background on hover, pressed look on `.active` or `[aria-pressed=true]` |
| `.btn-icon`, `-sm`, `-lg` | Square icon-only buttons; `-sm` is 24px, the floor for axe's target-size rule, and `-lg` is 44px |
| `.touch-target` | 44px minimum in both dimensions below `md` |
| `.focus-inset` | Draws the focus ring inside the control |
| `.btn[aria-disabled]`, `.dropdown-item[aria-disabled]` | Disabled look on a control that stays focusable |
| `.nav-pills-subtle` | Pills whose active state is the secondary background and primary text, not a solid fill |
| `.list-row` | A row holding a link plus its own controls |
| `.reveal-host` / `.reveal-target` | Controls shown on hover, focus-within or `.active`, and always shown where hover does not exist |
| `.dropdown-menu-fixed` | Fixed-position panel above the bulk-action bar |
| `.eyebrow` | The small uppercase label above a group |
| `.d-empty-none` | Hides a slot that projected nothing |
| `.avatar-circle` | A circle sized by `--avatar-size` |
| `.theme-light-only` / `.theme-dark-only` | Theme-specific artwork, switched by token rather than script |
| `.skip-link` | The shell's skip-to-content link |

Utilities are extended through the Bootstrap API rather than new rules: `.font-display`,
`.bg-surface`, `.mw-sm` / `.mw-md` / `.mw-prose`, `.min-w-0` (every flex ellipsis row needs it),
`.min-h-0`, `.text-pre-wrap`, `.min-dvh-100`, `.overscroll-contain` and `.border-dashed`. Monospace
text is Bootstrap's `.font-monospace`. `_kit.scss` also defines the `--safe-*` inset tokens and the
toast region's `--toast-top` / `--toast-right`.

`shared/<theme>/` holds the presentational, feature-agnostic building blocks, grouped by purpose:
`avatar/`, `badges/`, `brand/` (logo and ridgeline), `data-table/`, `pagination/`, `empty-state/`,
`icons/`, `dialog/` (modal, offcanvas), `menu/`, `overlay/` (tooltip, anchored panel), `search/`,
`file-drop/` and so on. Kit primitives stay there even when only one feature renders them. A
component that injects a store lives in the `core/` topic it serves instead: the theme toggle in
`core/theme/`, the error panel in `core/errors/`, the composer in `core/composer/`. Anything one
feature owns lives in that feature's folder. A dev-only `/ui-kit` route (`features/ui-kit/`) renders
the gallery, including every kit class and every overlay, and is the manual check that the top layer
behaves. `ui-kit` is a feature, and features never import each other, so the three components it
shows that other features own (`badges/permission-badge`, `badges/kind-badge`, `cards/project-card`)
live in `shared/`.

### Overlay primitives

The Angular logic is the app's own; the skin is Bootstrap's CSS. There is no Bootstrap JavaScript,
Popper or ng-bootstrap, and `check:forbidden` fails an import of any of them.

- **Modal.** `<dialog class="modal">` > `.modal-dialog` (centred, scrollable, `.modal-sm` / `.modal-lg`)
  > `.modal-content` with a header, `.modal-body` and a `.modal-footer` that is `d-empty-none`. A
  dismissible modal shows a `.btn-close[data-modal-dismiss]`, disabled while the modal is busy.
- **Offcanvas.** `<dialog class="offcanvas offcanvas-start|end">` with header, body and footer. In
  bare mode the header is visually hidden.
- **Dialog bridge.** `_overlays.scss` joins native `<dialog>` to Bootstrap's modal CSS: the
  user-agent reset, `::backdrop`, the scroll lock (`html:has(dialog[open])`), `display` for an open
  modal, and for the offcanvas hidden when closed, visible and pinned to its side when open. A
  closed offcanvas that is still `display: flex`, or an end offcanvas pinned left, leaks silently,
  so specs exercise both sides.
- **Initial focus.** `focusInitialTarget()` in `shared/dialog/tabbable.ts` picks the autofocus
  target, else the first tabbable that is not the close button, else the heading.
- **Menu.** The trigger is `btn-ghost btn-icon-sm`, or a bare reset when it wraps projected content.
  The panel is `.dropdown-menu.dropdown-menu-fixed.show`; `MenuItem` hosts as `.dropdown-item` and
  `MenuSeparator` as `.dropdown-divider`. Danger items use `text-danger-emphasis`.
- **Tooltip.** Renders `.tooltip.show.position-fixed` > `.tooltip-inner`, without an arrow.
- **Toasts.** `.toast-container` > `.toast.show` > `.toast-body` with a `.btn-close`. The tone edge and
  the drain bar are bespoke.
- **Project picker.** A `.dropdown-menu` of `.dropdown-item` rows marked `data-picker-row`.

Specs find elements by role and accessible name through `@testing-library/dom`, and
`src/testing/overlays.ts` holds the only places a spec names these overlay classes; see
[../development/testing-and-ci.md](../development/testing-and-ci.md).

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/styles/_tokens.scss` | The token maps, as data |
| `enterprise-gpt-ui/src/styles/_theme.scss` | Emits the maps per theme, plus `-rgb`, hover and active |
| `enterprise-gpt-ui/src/styles/_bootstrap-config.scss` | Feeds the palette and scales into Bootstrap's Sass; the utilities API and its extensions |
| `enterprise-gpt-ui/src/styles/_bootstrap-components.scss` | Imports every Bootstrap module |
| `enterprise-gpt-ui/src/styles/_focus.scss` | The focus floor |
| `enterprise-gpt-ui/src/styles/_overlays.scss` | The native `<dialog>` bridge for modal and offcanvas |
| `enterprise-gpt-ui/src/styles/_bootstrap-overrides.scss` | The colour flip loop, dark glyphs and the named focus rules |
| `enterprise-gpt-ui/src/styles/_kit.scss` | Classes Bootstrap lacks |
| `enterprise-gpt-ui/src/styles/_motion.scss` | The three app keyframes and the reduced-motion block |
| `enterprise-gpt-ui/src/app/shared/icons/icon-names.ts` | The sprite manifest |
| `enterprise-gpt-ui/src/testing/overlays.ts` | Spec helpers for menus, tooltips, dialogs and icons |
| `enterprise-gpt-ui/scripts/check-tokens.mjs` | Shell parity, contrast, breakpoint parity |
| `enterprise-gpt-ui/src/styles/motion.spec.ts` | No animation names a keyframe that does not exist |

## Related

- [layout-and-navigation.md](layout-and-navigation.md)
- [../development/testing-and-ci.md](../development/testing-and-ci.md)
