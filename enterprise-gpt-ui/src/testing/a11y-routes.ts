/**
 * Apart from `a11y.ts` so the jsdom coverage guard never loads `axe-core`. Add a route in the
 * commit that builds it. `/ui-kit` is excluded: it never matches in a production build.
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
