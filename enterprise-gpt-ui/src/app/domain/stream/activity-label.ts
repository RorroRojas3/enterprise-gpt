import { AssistantActivity } from './andes/assistant-ui.contract';

/**
 * The label one activity shows — on its card and in the live region, which must agree.
 *
 * Only `McpTool` activities are reworked. Since Andes 0.7.0 their `displayName` is the raw tool
 * name the model called, which for a tool this application leased is `{server}_{tool}`: the API
 * renames every leased tool before handing it to the tracking wrapper, so the prefix travels with
 * the name. Stripping it leaves the part that says what actually ran, and the server it belongs to
 * is already on the card as `source`.
 *
 * `Function` and `Agent` are returned untouched. A function name is a code identifier the design
 * sets in mono on purpose, and reflowing it into prose and then rendering that prose in a monospace
 * face is the worst of both; an agent's name is already a display string.
 *
 * This deliberately does not share `kind-badge`'s private `titleCase`, and should not be refactored
 * to: that one is the badge's fallback for a *kind* it does not recognise, leaves underscores alone
 * on purpose, and its behaviour is pinned by its own spec.
 */
export function activityLabel(
  activity: Pick<AssistantActivity, 'displayName' | 'kind' | 'source'>,
): string {
  if (activity.kind !== 'McpTool') {
    return activity.displayName;
  }

  // Trimmed because a stripped remainder can end up padded — a name ending in the separator leaves
  // one — and a trailing space is read aloud as a pause by the live region this also feeds.
  return humanize(stripServerPrefix(activity.displayName, activity.source)).trim();
}

/**
 * Removes the `{server}_` the API stamps onto a leased tool name.
 *
 * Every branch that cannot prove the prefix returns the name unchanged, because a wrong strip
 * produces a plausible-looking wrong label while an unstripped one is merely verbose. The match is
 * case-sensitive: the prefix and `source` are built from the same catalog string, so an exact
 * comparison is the expected case and a looser one could only ever remove more.
 *
 * Unlike the API's own longest-prefix-first search, there is nothing to disambiguate here — this
 * strips using the activity's own `source` rather than guessing which server a bare name came from.
 *
 * This only works because `source` is the *same* string the prefix was built from, which is a
 * property of the API rather than of this file: `McpToolProvider` renames each leased tool with
 * `SanitizeToolNamePrefix(server.Name)` and hands the tracking wrapper that same `server.Name`. If
 * that call ever reverts to the overload that reads the server's self-advertised name, every label
 * here silently stops stripping.
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

  // Emptiness is judged on the humanized form, not the raw one: the underscores become spaces a
  // moment later, so a remainder of "__" is a blank label even though it is not blank here — and a
  // blank label is exactly what this guard exists to prevent.
  return humanize(rest).trim() === '' ? name : rest;
}

/**
 * Mirrors `McpToolProvider.SanitizeToolNamePrefix`: every character that is not an ASCII letter,
 * digit, `_` or `-` becomes `_`.
 *
 * The class is spelled out rather than written `\w` so it reads as the mirror of the API's
 * `char.IsAsciiLetterOrDigit` it has to stay equal to. Both sides iterate UTF-16 code units, so a
 * character outside the basic plane yields two underscores on each.
 */
function sanitize(name: string): string {
  return name.replace(/[^A-Za-z0-9_-]/g, '_');
}

/**
 * Underscores to spaces, first character upper-cased.
 *
 * Applied even when no prefix was stripped, so an unrecognised name still reads as words — the
 * alternative puts raw underscores on screen and has a screen reader announce them.
 *
 * The rest of the string is left exactly as it was, so an acronym survives: `SEARCH_DOCS` becomes
 * `SEARCH DOCS`, not `Search docs`. `charAt` rather than `[0]`, which `noUncheckedIndexedAccess`
 * types as possibly undefined and which would render the literal string "undefined" if it were.
 */
function humanize(value: string): string {
  const spaced = value.replaceAll('_', ' ');

  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
