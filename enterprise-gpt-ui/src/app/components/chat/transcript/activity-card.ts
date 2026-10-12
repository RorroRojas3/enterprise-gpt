import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { AssistantActivity } from '@shared/models/andes/assistant-ui.contract';
import { activityLabel } from '@shared/utils/stream/activity-label';
import { Icon } from '@shared/components/icon/icon';
import { KindBadge } from './kind-badge';
import { formatActivityUsage, formatDurationSeconds } from './turn-usage';

/** Three levels are styled; anything deeper reuses the third's styling. */
const MAX_STYLED_DEPTH = 2;

let nextId = 0;

/**
 * The kind is a separate badge so no card reads "Calling Jira Cloud MCP", and `source` keeps the
 * server visible once `activityLabel` strips it from the label. Children render open; the
 * chevron reveals only the cost strip.
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

  /** The live region derives its own label from the same function; this one is not announced. */
  protected readonly label = computed(() => activityLabel(this.activity()));

  protected readonly expanded = signal(false);

  /** Null until the activity completes. */
  protected readonly duration = computed(() =>
    formatDurationSeconds(this.activity().durationSeconds),
  );

  /**
   * The state as text, since the card shows it by colour and shape alone. The duration tells a
   * timeout from an immediate refusal.
   */
  protected readonly stateLabel = computed(() => {
    const label = STATE_LABELS[this.activity().state];
    const duration = this.duration();

    return duration === null ? label : `${label} after ${duration}`;
  });

  /** The styling level, so an agent nested five deep still renders legibly. */
  protected readonly level = computed(() => Math.min(this.depth(), MAX_STYLED_DEPTH));

  protected readonly usageEntries = computed(() => {
    const activity = this.activity();

    return formatActivityUsage(activity.usage, activity.durationSeconds);
  });

  /** No chevron on a running activity: there is nothing settled to show yet. */
  protected readonly expandable = computed(() => this.usageEntries().length > 0);

  /** Quotes the rendered label, so a speech-input user can name the icon-only button by sight. */
  protected readonly toggleLabel = computed(() => `Cost of ${this.label() || 'this activity'}`);

  protected toggle(): void {
    this.expanded.update((expanded) => !expanded);
  }
}

const STATE_LABELS: Record<AssistantActivity['state'], string> = {
  Running: 'Running',
  Completed: 'Completed',
  Failed: 'Failed',
};
