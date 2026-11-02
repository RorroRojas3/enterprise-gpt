# Layout and Navigation

The shell, the sidebar, and how the application restructures below a desktop width.

## One breakpoint record, two copies, two gates

The application restructures at two widths and only two: **768px**, where tables become cards and
the sidebar leaves the flow, and **992px**, where the sidebar becomes persistent. 992px is Bootstrap's `lg`, so `*-lg-*` utilities
switch exactly where the app changes mode.

The two edges are stated once per language, because Sass cannot read a TypeScript constant and
`matchMedia` cannot read a Sass variable. In Sass the record also builds Bootstrap's `$grid-breakpoints`
(`md` from `$bp-tablet-min`, `lg` from `$bp-desktop-min`, the rest Bootstrap's defaults), which
`_bootstrap-config.scss` hands to Bootstrap, so the grid, `d-md-*` and `col-lg-*` follow the record.

| Mode | SCSS | TypeScript |
| --- | --- | --- |
| Mobile | `bp.mobile` | `MOBILE_VIEWPORT` — `(max-width: 767.98px)` |
| Tablet | `bp.tablet` | `TABLET_VIEWPORT` — `(min-width: 768px) and (max-width: 991.98px)` |
| Desktop | `bp.desktop` | both queries false |
| Under 992 | `bp.below-desktop` | — |
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
naming 767, 767.98, 768, 991.98 or 992 — exempting only the two record files. The failure message
names the mixins and constants, so the fix is in the failure.

## The three modes

| Mode | Width | Sidebar | Chrome above the screen |
| --- | --- | --- | --- |
| `desktop` | 992px and up | In the flow, 260px or a 60px strip — **persisted** | none |
| `tablet` | 768–991px | 60px strip by default; expands as a floating 260px panel over a backdrop — **not persisted** | none |
| `mobile` | under 768px | Not in the flow at all; lives in a modal drawer | a 54px navbar |

### Narrow desktops and the admin tables

From 992px the persistent sidebar and the admin rail sit beside the table, which leaves less room
than at 991px, where the sidebar was still a strip. `DataTable` drops its `hideOnTablet` columns
only in the tablet band, so the admin MCP and model tables take their columns from
`injectAdminColumns` (`features/admin/admin-columns.ts`). It drops them up to Bootstrap's `xxl` edge
(1400px) and keeps every column below 768px, where the table becomes cards.

Only the desktop width is persisted, in `UiStore`, which deliberately does not reset on sign-out —
it is a device-local preference, not the signed-in user's data.

## The shell

`features/shell/` is lazy, and so is everything it renders. That is what keeps
the signed-in chrome off the four unguarded auth routes, and it is why `/forbidden` is a *sibling* of the shell
rather than a child: it must render without chrome.

The shell composes the sidebar, the mobile navbar, and a `<router-outlet>`. A routed screen
publishes into the navbar through a seam rather than the navbar reaching into the screen.

The sidebar's two widths are separate template branches rather than one branch with things hidden: a
44px square target with a tooltip is a different control from a 36px row with a visible label, not
the same control with its text removed.

Toggling therefore destroys the collapse chevron and restores focus to the one that replaces it, in
the other branch, so the next Tab does not restart at the top of the page. `Tooltip` (below) only
shows for a focus the browser marks `:focus-visible`, so that restored focus can move the flyout to
the new chevron for a keyboard user without leaving it stuck open for a pointer user who never
hovered the control focus landed on.

Neither chevron carries `aria-expanded`: its name already states the direction ("Expand sidebar",
"Collapse sidebar"), and a state on top of it would be announced twice.

## Changing page

A client-side navigation gives a keyboard or screen-reader user no cue that anything happened: the
old page is gone, but focus stays where it was and no page load is announced. The shell supplies the
cue, and closes whichever sidebar overlay the navigation came from.

`injectPageChanges()` (`core/navigation/page-changes.ts`) turns router events into two signals:

| Event | Meaning |
| --- | --- |
| `page` | The path changed |
| `same` | A link to the URL the reader is already on (the router's ignored same-URL navigation) |

Everything else is silent, because the page is updating itself rather than being left. A query-only
navigation keeps the same path. A programmatic `replaceUrl` is the URL catching up to a conversation
the reader is already in. Back and Forward are the exception: the router marks them `replaceUrl`
too, so the navigation trigger (`popstate`) is what keeps them counted as `page`.

`Shell` reacts to both events by closing the mobile drawer and the tablet overlay: both are modal in
effect, so a navigation while one is up came from inside it. On `same` that is all. On `page` it also
settles the reader on the new page:

1. **Announce.** It writes the document title into a persistent `role="status"` region
   (`pageAnnouncement`). The region sits outside `<main>`, which can be `inert` and whose content is
   what changed. The routed screens supply the title; the project tabs (`instructions`, `files`,
   `conversations`) have static route titles for this reason.
2. **Focus `<main>`.** It focuses `#main-content` unless a `dialog[open]` exists or focus is already
   inside `<main>`, so the composer, a project tab or a page that placed focus itself keeps it.

The focus move waits until the closing overlay has finished with focus:

- **Mobile.** The shell waits for the drawer's `(closed)` output. The native dialog `close` event is
  queued, and `Offcanvas` restores focus to the hamburger inside it, which would overwrite a move
  made earlier.
- **Tablet and desktop.** The shell waits for `afterNextRender`, by which point `inert` has lifted
  from `<main>` and the element can take focus.

## Activity indicators

Because a turn keeps streaming after the user leaves its conversation, the shell has to show which
conversations are generating and which finished while nobody was looking. `ConversationActivityStore`
holds that (see [state.md](state.md#conversation-activity)); `ActivityIndicator` draws it (see
[design-system.md](design-system.md#activity-indicator)).

| Where | What it shows |
| --- | --- |
| Sidebar conversation row | The indicator sits **inside the row's link**, so its hidden label extends the link's accessible name. While the turn is running, the row's kebab also offers "Stop response", which stops it without opening the conversation |
| Sidebar project conversations | The same indicator at the end of each row's link |
| Conversations library | The indicator leads the name cell, so truncation never cuts it |
| Strip (collapsed sidebar) | A corner mark on the Chat link, since the strip lists no conversations. The mark is the one `summary`, and the link's `aria-label` appends `summarySuffix`, for example "Chat — 2 generating, 1 new answer" |
| Mobile navbar | The same corner mark on the hamburger, which stands for the conversations in the drawer; its `aria-label` reads "Open navigation — 2 generating, 1 new answer" |

On the two corner marks the indicator is unlabelled (`labelled=false`) because the control's own
`aria-label` already says it, and repeating it would be read twice. A running turn outranks an unseen
answer, which outranks an interrupted one, so a corner mark shows the most urgent of them.

## Leaving the page mid-turn

A reload or a closed tab ends every running turn, and the stream cannot be resumed. While any turn is
in flight, `UnloadPrompt` (`core/navigation/unload-prompt.ts`) asks the browser to confirm. Routing
inside the app never prompts, because the sessions survive it.

- **The `beforeunload` listener is attached only while a turn is running.** Any such listener keeps
  a page out of the back/forward cache, so an idle page must not carry one. `ChatSessions` registers
  its running count with `register(busy)`; the guard is the union of every registered source.
- **App-initiated leaves never prompt.** MSAL's redirects go through `leaving(fn)`, and
  `HardNavigation` calls `suspend()`. The prompt is the wrong thing to show mid-redirect, and
  cancelling it would leave MSAL's `interaction_in_progress` lock stranded. `Auth.signIn`,
  `Auth.signOut` and `AccessTokenClient` all wrap their redirects in `leaving()`. If the redirect
  rejects, the page stayed and the guard is re-armed; `AccessTokenClient` then suspends again for the
  one rejection that means a redirect is already committed.
- **`HardNavigation.assign(url)` resolves `url` against `document.baseURI`.** An origin-absolute path
  such as `/signed-out` would leave a deployment served from a sub-path; resolved against the base
  href it stays inside it.

## Overlays

The primitives are `anchored-panel` (positioning), `menu` (roving
focus), `modal` (native `<dialog>` with `showModal()`) and `offcanvas`, plus the `tooltip`
directive, in `shared/overlay/`, `shared/menu/` and `shared/dialog/`.

The primitives keep their Angular logic and take their look from Bootstrap's CSS: `modal` is a
`<dialog class="modal">` and `offcanvas` a `<dialog class="offcanvas offcanvas-start|end">`, joined to
Bootstrap's rules by `_overlays.scss`; `menu` panels are `.dropdown-menu`; the tooltip is
`.tooltip`. There is no Bootstrap JavaScript. The element-by-element mapping is in
[design-system.md](design-system.md#overlay-primitives).

Using the native dialog is what gives real inertness from the top layer rather than a hand-rolled
focus trap. Focus restoration runs on close **and** on destroy — see
[design-system.md](design-system.md).

`menu` restores focus to its trigger on close the same way, so a trigger that carries a `hint`
tooltip meets the same `:focus-visible` gate the sidebar chevron does.

## A stray file drop

`providePreventDropNavigation()` is global. Without it, a file dropped anywhere outside a drop target
would navigate the browser to that file, discarding an in-flight turn.

## Key files

| Path | Role |
| --- | --- |
| `enterprise-gpt-ui/src/app/shared/layout/breakpoints.ts` | The TypeScript half of the record |
| `enterprise-gpt-ui/src/styles/_breakpoints.scss` | The Sass half, its mixins and Bootstrap's `$grid-breakpoints` |
| `enterprise-gpt-ui/src/app/features/admin/admin-columns.ts` | Admin table columns up to the `xxl` edge |
| `enterprise-gpt-ui/src/styles/_overlays.scss` | The native `<dialog>` bridge for modal and offcanvas |
| `enterprise-gpt-ui/src/app/features/shell/shell.ts` | Mode derivation and composition |
| `enterprise-gpt-ui/src/app/core/navigation/page-changes.ts` | `injectPageChanges`: which navigations count as `page` or `same` |
| `enterprise-gpt-ui/src/app/core/a11y/recover-focus.ts` | `recoverFocusOn`: focus back to `#main-content` when a removal drops it; used by the toast region and the admin model and MCP tables |
| `enterprise-gpt-ui/src/app/core/navigation/navigation-failure.ts` | The toast a failed later navigation raises, with Reload |
| `enterprise-gpt-ui/src/app/features/shell/sidebar/sidebar.ts` | The two width branches |
| `enterprise-gpt-ui/src/app/features/shell/ui-store.ts` | Device-local layout preferences |
| `enterprise-gpt-ui/src/app/shared/badges/activity-indicator.ts` | The spinner, dot or warning shown on rows and corner marks |
| `enterprise-gpt-ui/src/app/core/navigation/unload-prompt.ts` | The `beforeunload` guard while a turn is in flight |
| `enterprise-gpt-ui/src/app/shared/{menu,dialog,overlay}/` | Menu, modal, offcanvas, the tooltip directive and `anchored-panel` |
| `enterprise-gpt-ui/src/app/shared/dialog/modal.spec.ts` | Focus restore on close and destroy |
| `enterprise-gpt-ui/src/app/core/navigation/unload-prompt.spec.ts` | When the listener is attached, suspended and re-armed |
| `enterprise-gpt-ui/src/app/core/navigation/page-changes.spec.ts` | Which navigations are `page`, `same` or silent |

## Related

- [design-system.md](design-system.md)
- [../architecture/frontend.md](../architecture/frontend.md)
