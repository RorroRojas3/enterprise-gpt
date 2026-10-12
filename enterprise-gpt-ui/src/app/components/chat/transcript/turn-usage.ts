import { UsageSummary } from '@shared/models/andes/assistant-ui.contract';

/**
 * `en-US` rather than the ambient locale: the design fixes `1,842`, and the host's locale would
 * make every spec asserting a count depend on where it ran.
 */
const TOKENS = new Intl.NumberFormat('en-US');

export function formatDurationSeconds(seconds: number | undefined): string | null {
  return seconds === undefined ? null : `${seconds.toFixed(1)}s`;
}

/**
 * A count a provider did not report is omitted, not rendered as zero. Null when the turn claims
 * nothing, as every turn that never folded a `Finished` does.
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
 * Two non-breaking spaces keep label and value apart in the mono face without a grid. The fold
 * never writes `AssistantActivity.usage` yet, so today the strip shows only the duration.
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
