import { ValidationAppError } from './app-error';
import { toCamelCase } from './camel-case';

/**
 * Plucks the messages a validation problem carries for one field.
 *
 * The Signal Forms counterpart to `applyServerErrors`: Signal Forms has no
 * `setErrors`, so server messages surface through a declarative `validate` rule that
 * reads them from store state rather than being written onto a control. The server
 * keys its `errors` dictionary by its own PascalCase property paths while fields are
 * named after the camelCase JSON the same DTO serializes to, so each key is
 * normalized with the server's own camel-casing algorithm before comparison, with a
 * case-insensitive match as the fallback.
 *
 * Flat keys only, deliberately: the forms shipped so far bind top-level properties.
 * The first form that binds a nested or indexed path (`Chunking.MaxTokens`,
 * `Files[0].FileName`) extends this with `applyServerErrors`' path parsing rather
 * than reimplementing it.
 *
 * @param error The normalized validation error, or null when there is none.
 * @param field The camelCase field name, e.g. `name`.
 */
export function serverMessagesFor(
  error: ValidationAppError | null,
  field: string,
): readonly string[] {
  if (error === null) {
    return [];
  }

  const messages: string[] = [];
  for (const [key, keyMessages] of Object.entries(error.errors)) {
    if (matchesField(key, field)) {
      messages.push(...keyMessages);
    }
  }

  return messages;
}

/**
 * The messages a validation problem carries for no listed field — object-level rules,
 * which FluentValidation keys with the empty string, and any property the form does
 * not model. The caller renders them as a summary so nothing the server said is
 * silently discarded; the counterpart of `applyServerErrors`' `unmatched` result.
 *
 * @param error The normalized validation error, or null when there is none.
 * @param fields The camelCase field names the form models.
 */
export function unmatchedServerMessages(
  error: ValidationAppError | null,
  fields: readonly string[],
): readonly string[] {
  if (error === null) {
    return [];
  }

  const messages: string[] = [];
  for (const [key, keyMessages] of Object.entries(error.errors)) {
    if (!fields.some((field) => matchesField(key, field))) {
      messages.push(...keyMessages);
    }
  }

  return messages;
}

function matchesField(key: string, field: string): boolean {
  return toCamelCase(key) === field || key.toLowerCase() === field.toLowerCase();
}
