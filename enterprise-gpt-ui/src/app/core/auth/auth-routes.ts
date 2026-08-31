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

/** Frame `6b`: the sign-in did not complete. */
export const LOGIN_FAILED_ROUTE = '/login-failed';

/** Frame `6c`: signed in, but `POST api/users/me` failed. */
export const SESSION_ERROR_ROUTE = '/session-error';

/** Frame `6e`: signed in, but not an administrator. */
export const FORBIDDEN_ROUTE = '/forbidden';

/**
 * Where sign-out lands. Unguarded, and it has to be: the whole point is a page that
 * does *not* start a new sign-in, so that a user leaving a shared machine is not
 * silently re-authenticated by an Entra session cookie the sign-out failed to end.
 */
export const SIGNED_OUT_ROUTE = '/signed-out';

/** EP-7's conversation library, frame `4a`. */
export const CONVERSATIONS_ROUTE = '/conversations';

/** EP-9's projects grid, frame `4c`. A project's detail screen is a child of it. */
export const PROJECTS_ROUTE = '/projects';

/** EP-10's documents library, frame `4j`. */
export const DOCUMENTS_ROUTE = '/documents';

/** The administration area. Behind `adminCanMatch`, so its chunk is withheld, not hidden. */
export const ADMIN_ROUTE = '/admin';
