import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { AssistantActivity } from '@domain/stream/andes/assistant-ui.contract';
import { KindBadge } from '@shared/badge/kind-badge/kind-badge';
import { Icon } from '@shared/icon/icon';
import { formatActivityUsage, formatDurationSeconds } from './turn-usage';

/** Frame `1f` draws three levels; anything deeper reuses the third's styling. */
const MAX_STYLED_DEPTH = 2;

let nextId = 0;

/**
 * One activity of the assistant's turn, as frame `1f` draws it (US-501), with
 * the work it invoked nested inside it (US-502) and its own cost behind a
 * chevron (US-504).
 *
 * `displayName` is the label and the kind is a **separate** badge — the
 * contract keeps them apart so no card ever reads "Calling Jira Cloud MCP".
 * `source` renders verbatim: the "Atlassian · host" composition is the
 * server's, arriving as one string.
 *
 * Nesting is a render concern only. The vendored fold already hangs a child
 * off the parent named by its `parentScopeId`, so this component walks
 * `children` and recurses into itself; the timeline index decides only which
 * cards are top-level. Children are **not** behind the chevron: the frame draws
 * them open, and hiding an agent's work behind a control is the opposite of
 * what the timeline is for. The chevron reveals the cost strip alone.
 */
@Component({
  selector: 'app-activity-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, KindBadge],
  templateUrl: './activity-card.html',
  styleUrl: './activity-card.scss',
})
export class ActivityCard {
  readonly activity = input.required<AssistantActivity>();

  /** How deep this card sits; 0 is a top-level card in the timeline. */
  readonly depth = input(0);

  protected readonly detailId = `activity-usage-${nextId++}`;

  protected readonly expanded = signal(false);

  /** Frame `1f` writes durations as `3.1s` in JetBrains Mono; absent until completion. */
  protected readonly duration = computed(() =>
    formatDurationSeconds(this.activity().durationSeconds),
  );

  /**
   * The state as text, because the frame carries it as colour and shape alone.
   * A settled activity carries its duration here too (US-505): the mono figure
   * beside the label is the only place it appears, and "Failed" without it says
   * nothing about whether the call timed out or refused immediately.
   */
  protected readonly stateLabel = computed(() => {
    const label = STATE_LABELS[this.activity().state];
    const duration = this.duration();

    return duration === null ? label : `${label} after ${duration}`;
  });

  /** The styling level, so an agent nested five deep still renders legibly. */
  protected readonly level = computed(() => Math.min(this.depth(), MAX_STYLED_DEPTH));

  /** Frame `1f`'s `input 1,204  output 88  duration 3.1s` strip (US-504). */
  protected readonly usageEntries = computed(() => {
    const activity = this.activity();

    return formatActivityUsage(activity.usage, activity.durationSeconds);
  });

  /** No chevron on a running activity: there is nothing settled to show yet. */
  protected readonly expandable = computed(() => this.usageEntries().length > 0);

  protected readonly toggleLabel = computed(
    () => `Cost of ${this.activity().displayName || 'this activity'}`,
  );

  protected toggle(): void {
    this.expanded.update((expanded) => !expanded);
  }
}

const STATE_LABELS: Record<AssistantActivity['state'], string> = {
  Running: 'Running',
  Completed: 'Completed',
  Failed: 'Failed',
};
