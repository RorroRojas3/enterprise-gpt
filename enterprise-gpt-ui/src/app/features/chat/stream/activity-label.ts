import { AssistantActivity } from '@shared/models/andes/assistant-ui.contract';

/**
 * Shared by the activity card and the live region so they agree. Only `McpTool` names change:
 * function names are identifiers set in mono, and agent names are already display text.
 */
export function activityLabel(
  activity: Pick<AssistantActivity, 'displayName' | 'kind' | 'source'>,
): string {
  if (activity.kind !== 'McpTool') {
    return activity.displayName;
  }

  // A name ending in the separator leaves a padded remainder, and the live region reads a
  // trailing space as a pause.
  return humanize(stripServerPrefix(activity.displayName, activity.source)).trim();
}

/**
 * Returns the name unchanged unless the prefix is proven, since a wrong strip looks plausible.
 * Relies on `McpToolProvider` prefixing with the same `server.Name` it sends as `source`.
 */
function stripServerPrefix(name: string, source: string | undefined): string {
  if (!source) {
    return name;
  }

  const prefix = `${sanitize(source)}_`;

  if (!name.startsWith(prefix)) {
    return name;
  }

  const rest = name.slice(prefix.length);

  // Judged on the humanized form: a remainder of "__" would become a blank label.
  return humanize(rest).trim() === '' ? name : rest;
}

/**
 * Mirrors the API's `McpToolProvider.SanitizeToolNamePrefix`, including its per-UTF-16-code-unit
 * iteration, so a non-BMP character yields two underscores on both sides.
 */
function sanitize(name: string): string {
  return name.replace(/[^A-Za-z0-9_-]/g, '_');
}

/**
 * Applied even without a strip, so raw underscores never reach a screen reader. Keeps the rest's
 * case so acronyms survive; deliberately not `kind-badge`'s `titleCase`, which keeps underscores.
 */
function humanize(value: string): string {
  const spaced = value.replaceAll('_', ' ');

  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
