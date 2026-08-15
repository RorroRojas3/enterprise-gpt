import { ValidationAppError } from '@core/errors/app-error';
import { serverMessagesFor } from '@core/errors/server-messages';

/**
 * The copy frame `4h` and US-903's second criterion require for a name already taken.
 *
 * It is not what the server sends. `ProjectService.CreateDuplicateNameException` writes
 * `A project named 'X' already exists.` — accurate, but it reads as a fact about the
 * system rather than as something the reader can act on, and it quotes back a name
 * already visible in the field above it.
 */
export const DUPLICATE_NAME_MESSAGE = 'You already have a project with this name.';

/**
 * Matches the server's duplicate-name message, and nothing else keyed to `Name`.
 *
 * The `errors` dictionary uses one key for every name failure — required, too long,
 * already taken — so the shape has to be read off the message. Anchored on the two
 * fixed halves the server's format string cannot lose; a message that stops matching
 * this falls through to being rendered verbatim, which is the safe direction.
 */
const DUPLICATE_NAME_PATTERN = /^A project named '.*' already exists\.$/;

/**
 * The messages to render under the name field for a rejected create or update.
 *
 * Every server message for `Name` is rendered, with the duplicate one swapped for the
 * board's wording. Swapping rather than appending, because two sentences saying the
 * same thing under one field is worse than either alone; and swapping only that one,
 * because "Names are limited to 256 characters" is the server's to word and nothing in
 * the design contradicts it.
 *
 * @param error The validation problem the server returned, if any.
 */
export function projectNameMessages(error: ValidationAppError | null): readonly string[] {
  return serverMessagesFor(error, 'name').map((message) =>
    DUPLICATE_NAME_PATTERN.test(message) ? DUPLICATE_NAME_MESSAGE : message,
  );
}
