import { ValidationAppError } from '@core/errors/app-error';

/** Second person and naming the way out, unlike the server's third-person refusal. */
export const SELF_REVOKE_ADMIN_MESSAGE =
  "You can't remove your own Administrator permission — another administrator must make this change.";

/** The same substitution for the self-deactivation refusal. */
export const SELF_DEACTIVATE_MESSAGE = "You can't deactivate your own account.";

/**
 * Matched on the `errors` key, never the server's prose, which a backend change can reword. The
 * `isSelf` check keeps a future rule under the same key from being blamed on the reader.
 */
export function isSelfActionRefusal(
  error: ValidationAppError | null,
  key: 'PermissionIds' | 'Id',
  isSelf: boolean,
): boolean {
  return isSelf && error !== null && key in error.errors;
}
