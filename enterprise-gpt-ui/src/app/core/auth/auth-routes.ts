/**
 * Lives in `core/` because MSAL's guard configuration, the guards and `main.ts` reference these,
 * and `core/` may not import from `features/`.
 */

/** Where a user lands after signing in, and the fallback for every ambiguous case. */
export const CHAT_ROUTE = '/chat';

/** The sign-in did not complete. */
export const LOGIN_FAILED_ROUTE = '/login-failed';

/** Signed in, but `POST api/users/me` failed. */
export const SESSION_ERROR_ROUTE = '/session-error';

/** Signed in, but not an administrator. */
export const FORBIDDEN_ROUTE = '/forbidden';

/**
 * Unguarded on purpose: it must not start a new sign-in, or an Entra session cookie the sign-out
 * failed to end would silently re-authenticate a user leaving a shared machine.
 */
export const SIGNED_OUT_ROUTE = '/signed-out';

export const CONVERSATIONS_ROUTE = '/conversations';

/** A project's detail screen is a child of it. */
export const PROJECTS_ROUTE = '/projects';

export const DOCUMENTS_ROUTE = '/documents';

/** The administration area. Behind `adminCanMatch`, so its chunk is withheld, not hidden. */
export const ADMIN_ROUTE = '/admin';
