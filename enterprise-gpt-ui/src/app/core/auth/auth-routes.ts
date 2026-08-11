/**
 * The route paths the authentication layer navigates to.
 *
 * They live in `core/` rather than beside the components because MSAL's guard
 * configuration, three guards and `main.ts` all reference them, and `core/` may not
 * import from `features/`. `app.routes.ts` is the one place that maps them onto
 * components.
 */

/** Where a user lands after signing in, and the fallback for every ambiguous case. */
export const CHAT_ROUTE = '/chat';

/** The MSAL redirect URI. Never guarded — MSAL refuses to activate its own target. */
export const AUTH_ROUTE = '/auth';

/** Frame `6b`: the sign-in did not complete. */
export const LOGIN_FAILED_ROUTE = '/login-failed';

/** Frame `6c`: signed in, but `POST api/users/me` failed. */
export const SESSION_ERROR_ROUTE = '/session-error';

/** Frame `6e`: signed in, but not an administrator. */
export const FORBIDDEN_ROUTE = '/forbidden';

/** The administration area. Behind `adminCanMatch`, so its chunk is withheld, not hidden. */
export const ADMIN_ROUTE = '/admin';
