import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  AssistantActivity,
  AssistantStatusSnapshot,
} from '@domain/stream/andes/assistant-ui.contract';
import { TurnTimeline, findActivity } from '@domain/stream/turn-timeline';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { ActivityCard } from './activity-card';

type RenderedNode =
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'activity'; readonly activity: AssistantActivity };

/**
 * One assistant turn: the avatar column and, in arrival order, the turn's text
 * blocks and activity cards (US-406, US-501).
 *
 * The order comes from the timeline index and every datum on a card comes from
 * the folded snapshot — the render-time join of the two structures the store
 * keeps separate. Text renders as plain pre-wrapped text: markdown is US-601,
 * gated on the bundle question, and rendering it here now would put an
 * unsanitized surface on screen.
 */
@Component({
  selector: 'app-assistant-turn',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ActivityCard, BrandLogo],
  templateUrl: './assistant-turn.html',
  styleUrl: './assistant-turn.scss',
})
export class AssistantTurn {
  readonly snapshot = input.required<AssistantStatusSnapshot>();
  readonly timeline = input.required<TurnTimeline>();
  /** Draws frame `1b`'s blinking caret at the end of the growing text. */
  readonly streaming = input<boolean>(false);

  /**
   * The join, derived once per flush rather than per node in the template. An
   * activity node whose scope the snapshot cannot resolve is dropped here —
   * defensive only, since both structures are fed the same events.
   */
  protected readonly renderedNodes = computed<readonly RenderedNode[]>(() => {
    const snapshot = this.snapshot();
    const text = snapshot.text ?? '';
    const nodes: RenderedNode[] = [];

    for (const node of this.timeline().nodes) {
      if (node.kind === 'text') {
        nodes.push({ kind: 'text', text: text.slice(node.start, node.end) });
      } else {
        const activity = findActivity(snapshot.activities, node.scopeId);
        if (activity !== null) {
          nodes.push({ kind: 'activity', activity });
        }
      }
    }

    return nodes;
  });

  /** The caret belongs to the last node, and only when it is a text block. */
  protected readonly caretIndex = computed(() => {
    const nodes = this.renderedNodes();
    const last = nodes[nodes.length - 1];

    return this.streaming() && last !== undefined && last.kind === 'text' ? nodes.length - 1 : -1;
  });
}
