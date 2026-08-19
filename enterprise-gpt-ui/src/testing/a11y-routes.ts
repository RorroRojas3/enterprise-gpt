/**
 * The routes the accessibility audit covers.
 *
 * A module of its own, separate from `a11y.ts`, for one reason: that file imports
 * `axe-core` at the top level, and `a11y-coverage.spec.ts` runs in the **jsdom** suite
 * where it needs this list and not a 600 kB auditor.
 *
 * The list is a record, under the rule the admin epic settled: a route is added by the
 * story that builds it, in the same commit as the route, so the audit can never
 * quietly not cover a screen that shipped. `/documents` arrived that way with US-1002,
 * which completed US-1405's four named route groups.
 *
 * `/ui-kit` is deliberately excluded: it is `canMatch: [() => isDevMode()]` and is never
 * matched in a production build, so a violation there ships to nobody.
 */
export const AXE_ROUTES: readonly string[] = [
  '/chat',
  '/conversations',
  '/projects',
  '/documents',
  '/admin/users',
  '/admin/models',
  '/admin/mcps',
  '/admin/reports',
];
