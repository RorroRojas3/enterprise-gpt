import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MessageAttachmentDto, MessageFeedbackRating } from '@domain/api/conversation';
import {
  AssistantActivity,
  AssistantStatusSnapshot,
} from '@domain/stream/andes/assistant-ui.contract';
import {
  StreamSplit,
  splitStreamingMarkdown,
  stripFenceInfo,
} from '@domain/markdown/streaming-split';
import { EmailSegment, detectEmailDraft, splitEmailSegments } from '@domain/email/email-draft';
import { TurnTimeline } from '@domain/stream/turn-timeline';
import { MarkdownComponent } from 'ngx-markdown';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { MarkdownExtras } from '../markdown/markdown-extras';
import { ActivityCard } from './activity-card';
import { EmailCard } from './email-card';
import { EmailOpenMenu } from './email-open-menu';
import { GeneratedFiles } from './generated-files';
import { MessageCopy } from './message-copy';
import { MessageFeedback } from './message-feedback';
import { ReasoningRegion } from './reasoning-region';
import { formatTurnUsage } from './turn-usage';

type RenderedNode =
  // `start` is carried through only as a stable identity for the template's
  // `track`: the offset a block begins at never changes once it exists.
  | {
      readonly kind: 'text';
      readonly text: string;
      readonly start: number;
      readonly segments: readonly EmailSegment[];
    }
  | { readonly kind: 'activity'; readonly activity: AssistantActivity };

/**
 * One assistant turn: the avatar column and, in arrival order, the turn's text
 * blocks and activity cards (US-406, US-501, US-502).
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
  imports: [
    ActivityCard,
    BrandLogo,
    EmailCard,
    EmailOpenMenu,
    GeneratedFiles,
    MarkdownComponent,
    MarkdownExtras,
    MessageCopy,
    MessageFeedback,
    ReasoningRegion,
  ],
  templateUrl: './assistant-turn.html',
  styleUrl: './assistant-turn.scss',
})
export class AssistantTurn {
  readonly snapshot = input.required<AssistantStatusSnapshot>();
  readonly timeline = input.required<TurnTimeline>();
  /** Draws frame `1b`'s blinking caret at the end of the growing text. */
  readonly streaming = input<boolean>(false);
  /**
   * The model's own reasoning summary (US-503), which is *not* read off the
   * snapshot: `snapshot.reasoningText` also carries the server's seeded first
   * delta, and only the store knows how long that was.
   */
  readonly reasoningText = input('');
  /** The measured reasoning window for frame `1f`'s pill (US-503). */
  readonly reasoningSeconds = input<number | null>(null);
  /**
   * The persisted message this turn became (US-1101), or null when it never became one —
   * a live turn, a cut-off, or a completed turn whose transcript write failed. Null is
   * what withholds the feedback controls, and it withholds them by construction rather
   * than by a flag anyone has to remember to set.
   */
  readonly messageId = input<string | null>(null);
  /** The rating already recorded against this answer (US-1103). */
  readonly rating = input<MessageFeedbackRating | null>(null);
  /** Whether this answer's own rating request is in flight (US-1103). */
  readonly ratingPending = input<boolean>(false);
  /** The files this turn produced, empty when it produced none. */
  readonly attachments = input<readonly MessageAttachmentDto[]>([]);
  /** The conversation a chip's download is addressed to. */
  readonly conversationId = input<string | null>(null);
  /** The thumb the reader pressed. What it means is the store's decision, not this one's. */
  readonly rated = output<MessageFeedbackRating>();

  /**
   * The join, derived once per flush rather than per node in the template.
   *
   * An activity node opens a top-level card only when its scope is a **root**
   * of the folded snapshot; anything else is nested work, which `ActivityCard`
   * renders inside its parent (US-502). Root membership is the test, not
   * `node.parentScopeId !== undefined`: the fold promotes a child whose parent
   * scope it has never seen to a root, and that card has to stay visible —
   * an unattached card is a rendering imperfection, a dropped one is a lost
   * tool call. It is also what survives a parent that arrives after its child.
   *
   * Roots are **consumed** rather than looked up: each scope id owns a queue of
   * the roots that arrived under it, and a node takes the next one. A plain
   * lookup keyed on `scopeId` would render one activity twice if two roots
   * arrived without a scope id, which the fold allows — it defaults a missing
   * id to the empty string rather than dropping the event. A single pointer
   * along `activities` would fix that too, but the invariant it rests on lives
   * in the vendored contract, and one mismatch would stall it and drop every
   * later card; a per-id queue costs one card instead.
   */
  protected readonly renderedNodes = computed<readonly RenderedNode[]>(() => {
    const snapshot = this.snapshot();
    const text = snapshot.text ?? '';
    const nodes: RenderedNode[] = [];
    const roots = new Map<string, AssistantActivity[]>();

    for (const root of snapshot.activities) {
      const queued = roots.get(root.scopeId);
      if (queued === undefined) {
        roots.set(root.scopeId, [root]);
      } else {
        queued.push(root);
      }
    }

    for (const node of this.timeline().nodes) {
      if (node.kind === 'text') {
        const slice = text.slice(node.start, node.end);
        // Split per node rather than over the whole answer: the slices are what
        // the template renders, and an email interrupted by a tool call lands in
        // neither half as a closed fence, so it stays markdown — the honest
        // rendering of a block that was never completed in one piece.
        nodes.push({
          kind: 'text',
          text: slice,
          start: node.start,
          segments: splitEmailSegments(slice),
        });
      } else {
        const activity = roots.get(node.scopeId)?.shift();
        if (activity !== undefined) {
          nodes.push({ kind: 'activity', activity });
        }
      }
    }

    return nodes;
  });

  /**
   * The turn's token counts for the message footer (US-504), or null when the
   * turn claims none. Null is the honest answer for every turn that never
   * folded a `Finished`: stopped, cut off, or replayed from the transcript,
   * where the stream that carried the counts is long gone.
   */
  protected readonly usageLine = computed(() => formatTurnUsage(this.snapshot().usage));

  /**
   * What the footer's Copy control puts on the clipboard (US-607): the answer's
   * original markdown, not the rendered HTML.
   *
   * Read off the snapshot rather than re-joined from {@link renderedNodes},
   * whose slices exist to be interleaved with activity cards in arrival order.
   * They happen to cover the whole text today, but that is the timeline's
   * contract to change, and a copy that silently lost a block would look like a
   * clipboard fault rather than a rendering one.
   */
  protected readonly copySource = computed(() => this.snapshot().text ?? '');

  /**
   * The footer's Open Email control, for an answer that is an email the model
   * never marked as one — an older transcript, or a model that ignored the
   * instruction.
   *
   * Null whenever a card already rendered: the card carries the same control,
   * and offering it twice would ask the reader which of two identical buttons is
   * the real one. Like the feedback guard beside it in the template, this is
   * fixed for the life of a settled entry, so the `@if` it drives cannot toggle
   * under the transcript's ResizeObserver.
   */
  protected readonly footerDraft = computed(() => {
    const hasCard = this.renderedNodes().some(
      (node) => node.kind === 'text' && node.segments.some((part) => part.kind === 'email'),
    );

    return hasCard ? null : detectEmailDraft(this.copySource());
  });

  /**
   * Whether the answer is still accumulating, for the live region's own
   * `aria-busy` (US-1402).
   *
   * The region is polite for the whole of a streaming turn — that is the
   * criterion — and `aria-busy` is what stops that meaning "read every delta":
   * a busy live region accumulates its changes and exposes them once, when busy
   * clears. It is set here as well as on the transcript because a region's own
   * `aria-busy` is the better-honoured of the two, and it reads the folded
   * `Finished` off the snapshot this component already receives.
   */
  protected readonly liveAnswer = computed(
    () => this.streaming() && this.snapshot().phase !== 'Completed',
  );

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
