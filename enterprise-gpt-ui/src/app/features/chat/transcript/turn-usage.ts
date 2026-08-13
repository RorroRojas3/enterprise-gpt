import { UsageSummary } from '@domain/stream/andes/assistant-ui.contract';

/**
 * Pinned to `en-US` rather than the ambient locale: the app ships one language,
 * the design fixes the grouping separator (`1,842`), and a formatter that
 * followed the host would render `1.842` on a German machine and make every
 * spec that asserts a count depend on where it ran.
 */
const TOKENS = new Intl.NumberFormat('en-US');

/** Frame `1f` writes every elapsed time as `3.1s`, in JetBrains Mono. */
export function formatDurationSeconds(seconds: number | undefined): string | null {
  return seconds === undefined ? null : `${seconds.toFixed(1)}s`;
}

/**
 * The turn's token counts as frames `1f` and `1g` write them —
 * `1,842 in · 512 out · 2,354 total` (US-504).
 *
 * Each count is optional on the wire because a provider may report only some,
 * so a missing one is **omitted rather than rendered as zero**: a turn that
 * reported no output tokens did not produce none. Null when the turn claims
 * nothing at all, which is every turn that never folded a `Finished` — stopped,
 * cut off, or replayed from the stored transcript.
 */
export function formatTurnUsage(usage: UsageSummary | undefined): string | null {
  if (usage === undefined) {
    return null;
  }

  const parts: string[] = [];
  if (usage.inputTokens !== undefined) {
    parts.push(`${TOKENS.format(usage.inputTokens)} in`);
  }
  if (usage.outputTokens !== undefined) {
    parts.push(`${TOKENS.format(usage.outputTokens)} out`);
  }
  if (usage.totalTokens !== undefined) {
    parts.push(`${TOKENS.format(usage.totalTokens)} total`);
  }

  return parts.length === 0 ? null : parts.join(' · ');
}

/**
 * One activity's split for the expanded card's strip — `input  1,204`,
 * `output  88`, `duration  3.1s` (frame `1f`, US-504).
 *
 * The label and its value are separated by two non-breaking spaces, as the
 * frame writes them, so the columns survive the mono face without a grid.
 *
 * `usage` is currently absent from every activity this server sends: the
 * streaming contract carries `usage` on `Finished` only, and the vendored fold
 * never writes `AssistantActivity.usage`. The strip therefore ships showing the
 * duration, and gains the token columns the moment a contract upgrade starts
 * attributing them — the field is already in the type.
 */
export function formatActivityUsage(
  usage: UsageSummary | undefined,
  durationSeconds: number | undefined,
): readonly string[] {
  const entries: string[] = [];
  if (usage?.inputTokens !== undefined) {
    entries.push(entry('input', TOKENS.format(usage.inputTokens)));
  }
  if (usage?.outputTokens !== undefined) {
    entries.push(entry('output', TOKENS.format(usage.outputTokens)));
  }
  if (usage?.totalTokens !== undefined) {
    entries.push(entry('total', TOKENS.format(usage.totalTokens)));
  }
  const duration = formatDurationSeconds(durationSeconds);
  if (duration !== null) {
    entries.push(entry('duration', duration));
  }

  return entries;
}

function entry(label: string, value: string): string {
  return `${label}\u00a0\u00a0${value}`;
}
