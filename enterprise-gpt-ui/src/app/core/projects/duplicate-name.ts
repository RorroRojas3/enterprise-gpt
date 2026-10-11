import { ValidationAppError } from '@core/errors/app-error';
import { serverMessagesFor } from '@core/errors/server-messages';

/** Replaces the server's "A project named 'X' already exists.", which echoes the field above. */
export const DUPLICATE_NAME_MESSAGE = 'You already have a project with this name.';

/**
 * `errors` uses one `Name` key for every failure, so the duplicate is told by its message. A
 * message that stops matching renders verbatim, which is the safe direction.
 */
const DUPLICATE_NAME_PATTERN = /^A project named '.*' already exists\.$/;

/**
 * Every server message for `Name`, with only the duplicate one swapped rather than appended, so
 * the field never says the same thing twice.
 */
export function projectNameMessages(error: ValidationAppError | null): readonly string[] {
  return serverMessagesFor(error, 'name').map((message) =>
    DUPLICATE_NAME_PATTERN.test(message) ? DUPLICATE_NAME_MESSAGE : message,
  );
}
