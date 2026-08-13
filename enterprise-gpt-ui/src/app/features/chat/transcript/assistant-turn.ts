import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  AssistantActivity,
  AssistantStatusSnapshot,
} from '@domain/stream/andes/assistant-ui.contract';
import {
  StreamSplit,
  splitStreamingMarkdown,
  stripFenceInfo,
} from '@domain/markdown/streaming-split';
import { TurnTimeline, findActivity } from '@domain/stream/turn-timeline';
import { MarkdownComponent } from 'ngx-markdown';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { ActivityCard } from './activity-card';

type RenderedNode =
  // `start` is carried through only as a stable identity for the template's
  // `track`: the offset a block begins at never changes once it exists.
  | { readonly kind: 'text'; readonly text: string; readonly start: number }
  | { readonly kind: 'activity'; readonly activity: AssistantActivity };

/**
 * One assistant turn: the avatar column and, in arrival order, the turn's text
 * blocks and activity cards (US-406, US-501).
 *
 * The order comes from the timeline index and every datum on a card comes from
 * the folded snapshot — the render-time join of the two structures the store
 * keeps separate.
 *
 * Text renders as markdown, sanitized at the parser and again at the DOM
 * boundary by the profile `provideChatMarkdown` installs (US-601). Each text
 * node is its own `<markdown>`: a node closed by an intervening activity never
 * changes again, and re-deriving its identical slice per flush leaves the input
 * value equal, so only the node still growing is ever re-parsed.
 */
@Component({
  selector: 'app-assistant-turn',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ActivityCard, BrandLogo, MarkdownComponent],
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
        nodes.push({ kind: 'text', text: text.slice(node.start, node.end), start: node.start });
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

  /**
   * The growing node cut at its last block boundary (US-602).
   *
   * Only this node is ever re-parsed mid-turn, and splitting it means only the
   * part after the boundary is: `head` is byte-identical between flushes until a
   * new block closes, and an unchanged string never reaches the renderer as a
   * changed input.
   */
  private readonly liveSplit = computed<StreamSplit>(() => {
    const index = this.caretIndex();
    const last = index === -1 ? undefined : this.renderedNodes()[index];

    return last?.kind === 'text' ? splitStreamingMarkdown(last.text) : { head: '', tail: '' };
  });

  protected readonly liveHead = computed(() => this.liveSplit().head);

  /**
   * The tail renders without its fence info strings, so a code block that is
   * still arriving is not highlighted against a grammar its half-written
   * contents may not satisfy. The settled render uses the untouched source.
   */
  protected readonly liveTail = computed(() => stripFenceInfo(this.liveSplit().tail));
}
