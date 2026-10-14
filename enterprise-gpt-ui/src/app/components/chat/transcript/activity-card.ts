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
 * chevron reveals only the cost strip. A group named by its label, so the tree reads as one.
 */
@Component({
  selector: 'app-activity-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, KindBadge],
  templateUrl: './activity-card.html',
  host: { class: 'd-block' },
})
export class ActivityCard {
  readonly activity = input.required<AssistantActivity>();

  /** How deep this card sits; 0 is a top-level card in the timeline. */
  readonly depth = input(0);

  private readonly id = nextId++;
  protected readonly detailId = `activity-usage-${this.id}`;
  protected readonly nameId = `activity-name-${this.id}`;

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

  /** Alternates the fill per level, so each child stands off the card it sits on. */
  protected readonly cardClass = computed(() => {
    switch (this.level()) {
      case 0:
        return 'border rounded-4 bg-surface px-3 py-2';
      case 1:
        return 'border rounded-3 bg-body-tertiary px-2 py-2';
      default:
        return 'border rounded-3 bg-surface px-2 py-2';
    }
  });

  /** The cost strip borrows the fill its card is not using, or it would vanish into it. */
  protected readonly usageFill = computed(() =>
    this.level() === 1 ? 'bg-surface' : 'bg-body-tertiary',
  );

  /**
   * Only the outermost rail insets below md: compounding ~40px per level leaves a third-level
   * card no room for its label at 390px. Deeper rails keep the border.
   */
  protected readonly childrenInset = computed(() =>
    this.level() === 0 ? 'ms-4 ps-3' : 'ms-0 ms-md-4 ps-2 ps-md-3',
  );

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
