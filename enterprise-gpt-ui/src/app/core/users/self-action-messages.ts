import { ValidationAppError } from '@core/errors/app-error';

/**
 * What frame `5d` says when an administrator tries to revoke their own Administrator
 * permission (US-1203).
 *
 * The server says "Administrators cannot revoke their own Administrator permission."
 * — accurate, third-person, and about a class of people rather than about the reader.
 * The board words it back at them and names the way out, which is the message the
 * acceptance criterion quotes.
 */
export const SELF_REVOKE_ADMIN_MESSAGE =
  "You can't remove your own Administrator permission — another administrator must make this change.";

/** The same substitution for frame `5d`'s second state (US-1204). */
export const SELF_DEACTIVATE_MESSAGE = "You can't deactivate your own account.";

/**
 * Whether a 400 is the server refusing a caller's edit to their **own** row.
 *
 * Matched on the `errors` key and the target's identity, never on the server's prose:
 * that message is display text a backend change can reword, and matching it would make
 * this silently stop working rather than fail a test.
 *
 * The identity half is what makes the key sufficient. `PermissionIds` is also the subject
 * of the request validator's per-element rule, but FluentValidation keys that one
 * `PermissionIds[0]` — so the bare key on the update route carries the self-revocation
 * refusal and nothing else. The identity check still earns its place: it stops a future
 * server-side rule under the same key from being reported to the reader as something they
 * did to themselves.
 *
 * @param error The normalized validation problem.
 * @param key The server's PascalCase property name — `PermissionIds` on the update
 *   route, `Id` on the deactivate one.
 * @param isSelf Whether the row being acted on is the signed-in administrator's.
 */
export function isSelfActionRefusal(
  error: ValidationAppError | null,
  key: 'PermissionIds' | 'Id',
  isSelf: boolean,
): boolean {
  return isSelf && error !== null && key in error.errors;
}
