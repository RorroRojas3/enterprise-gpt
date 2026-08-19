/**
 * The routes the accessibility audit covers.
 *
 * A module of its own, separate from `a11y.ts`, for one reason: that file imports
 * `axe-core` at the top level, and `a11y-coverage.spec.ts` runs in the **jsdom** suite
 * where it needs this list and not a 600 kB auditor.
 *
 * **`/documents` is absent, and that is not an oversight.** US-1405's criterion names it,
 * but no such route exists: EP-10 is unstarted and US-1002 is the story that creates it,
 * under the rule the admin epic settled — a route is added by the story that builds it.
 * The list is a record so that story adds its entry in the same commit as its route,
 * rather than the audit quietly never covering it.
 *
 * `/ui-kit` is deliberately excluded: it is `canMatch: [() => isDevMode()]` and is never
 * matched in a production build, so a violation there ships to nobody.
 */
export const AXE_ROUTES: readonly string[] = [
  '/chat',
  '/conversations',
  '/projects',
  '/admin/users',
  '/admin/models',
  '/admin/mcps',
  '/admin/reports',
];
