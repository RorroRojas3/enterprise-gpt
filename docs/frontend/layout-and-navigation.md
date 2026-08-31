# Layout and Navigation

The shell, the sidebar, and how the application restructures below a desktop width.

## One breakpoint record, two copies, two gates

The application restructures at two widths and only two: **768px**, where tables become cards and
the sidebar leaves the flow, and **1024px**, where the sidebar stops being persistent.

The two edges are stated once per language, because Sass cannot read a TypeScript constant and
`matchMedia` cannot read a Sass variable.

| Mode | SCSS | TypeScript |
| --- | --- | --- |
| Mobile | `bp.mobile` | `MOBILE_VIEWPORT` — `(max-width: 767.98px)` |
| Tablet | `bp.tablet` | `TABLET_VIEWPORT` — `(min-width: 768px) and (max-width: 1023.98px)` |
| Desktop | `bp.desktop` | both queries false |
| Under 1024 | `bp.below-desktop` | — |
| 768 and up | `bp.tablet-up` | — |
| As numbers | `$bp-tablet-min`, `$bp-desktop-min` | `TABLET_MIN_WIDTH`, `DESKTOP_MIN_WIDTH` |

**`767px` and `767.98px` are not the same edge.** `(max-width: 767px)` beside `(min-width: 768px)`
leaves a dead zone: a viewport reported as 767.5px — browser zoom, or Windows display scaling —
matches *neither* query, and the user gets no sidebar and no navbar either.

**The two queries are mutually exclusive and desktop is the fall-through.** A `mode` derived from
them must read `desktop` when both are false, because the test fake answers `false` for every query
a spec has not set — so "nothing configured" has to mean the mode the existing suite was written
against.

Call sites use mixins rather than bare variables, because
`@media (max-width: $bp-tablet-min - 0.02px)` written twelve times is twelve chances to write
`- 1px` instead.

**Deliberately not in the record:** a component reflowing its own contents at 575px or 480px. Those
are not the application changing mode, and folding them in would make the record mean less.

### The two gates

`check:tokens` compares the two copies on every lint, in both halves: the whole-number minima, and
**every maximum must be exactly 0.02px below a declared minimum** — the half that rots silently,
because a `767px` maximum looks tidier and reintroduces the dead zone. The comparison rounds rather
than comparing raw floats. Both files are comment-stripped before scanning, because both document
the bug by quoting the wrong queries and scanning the prose would fail the gate on its own
explanation.

`check:forbidden` refuses a hardcoded application breakpoint — any `(max-width:` or `(min-width:`
naming 767, 767.98, 768, 1023.98 or 1024 — exempting only the two record files. The failure message
names the mixins and constants, so the fix is in the failure.

## The three modes

| Mode | Width | Sidebar | Chrome above the screen |
| --- | --- | --- | --- |
| `desktop` | 1024px and up | In the flow, 260px or a 60px strip — **persisted** | none |
| `tablet` | 768–1023px | 60px strip by default; expands as a floating 260px panel over a backdrop — **not persisted** | none |
| `mobile` | under 768px | Not in the flow at all; lives in a modal drawer | a 54px navbar |

Only the desktop width is persisted, in `UiStore`, which deliberately does not reset on sign-out —
it is a device-local preference, not the signed-in user's data.

## The shell

`features/shell/` is lazy, and everything under it is lazy too. That is what keeps the signed-in
chrome off the four unguarded auth routes, and it is why `/forbidden` is a *sibling* of the shell
rather than a child: it must render without chrome.

The shell composes the sidebar, the mobile navbar, and a `<router-outlet>`. A routed screen
publishes into the navbar through a seam rather than the navbar reaching into the screen.

The sidebar's two widths are separate template branches rather than one branch with things hidden: a
44px square target with a tooltip is a different control from a 36px row with a visible label, not
the same control with its text removed.

## Overlays

`shared/overlay/` holds the primitives: `anchored-panel` (positioning), `menu` (roving focus),
`modal` (native `<dialog>` with `showModal()`), `offcanvas`, and `tooltip`.

Using the native dialog is what gives real inertness from the top layer rather than a hand-rolled
focus trap. Focus restoration runs on close **and** on destroy — see
[design-system.md](design-system.md).

## A stray file drop

`providePreventDropNavigation()` is global. Without it, a file dropped anywhere outside a drop target
would navigate the browser to that file, discarding an in-flight turn.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts` | The TypeScript half of the record |
| `enterprise-gpt-ui/src/styles/_breakpoints.scss` | The Sass half and its mixins |
| `enterprise-gpt-ui/src/app/features/shell/shell.ts` | Mode derivation and composition |
| `enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts` | The two width branches |
| `enterprise-gpt-ui/src/app/core/ui/ui-store.ts` | Device-local layout preferences |
| `enterprise-gpt-ui/src/app/shared/overlay/` | Menu, modal, offcanvas, tooltip, anchored panel |
| `enterprise-gpt-ui/src/app/shared/overlay/modal/modal.spec.ts` | Focus restore on close and destroy |

## Related

- [design-system.md](design-system.md)
- [../architecture/frontend.md](../architecture/frontend.md)
