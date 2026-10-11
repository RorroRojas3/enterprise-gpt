import { ValidationAppError } from './app-error';
import { toCamelCase } from './camel-case';

/**
 * One field's server messages, for a Signal Forms `validate` rule (there is no `setErrors`).
 * PascalCase keys match via .NET camel-casing, then case-insensitively; flat keys only.
 *
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
 * Messages for no listed field (object-level rules keyed `''`, unmodelled properties), which the
 * caller renders as a summary so none are silently dropped.
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
