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
  // `start` is the template's `track` key: a block's start offset never changes.
  | {
      readonly kind: 'text';
      readonly text: string;
      readonly start: number;
      readonly segments: readonly EmailSegment[];
    }
  | { readonly kind: 'activity'; readonly activity: AssistantActivity };

/**
 * Joins the timeline's order with the folded snapshot's data. Each text node is its own
 * `<markdown>`, so only the node still growing is re-parsed.
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
  /** Draws the blinking caret at the end of the growing text. */
  readonly streaming = input<boolean>(false);
  /** Not read off the snapshot, whose `reasoningText` includes the server's seeded delta. */
  readonly reasoningText = input('');
  readonly reasoningSeconds = input<number | null>(null);
  /** Null for a live, cut-off or unpersisted turn, which withholds the feedback controls. */
  readonly messageId = input<string | null>(null);
  readonly rating = input<MessageFeedbackRating | null>(null);
  readonly ratingPending = input<boolean>(false);
  readonly attachments = input<readonly MessageAttachmentDto[]>([]);
  readonly conversationId = input<string | null>(null);
  /** What the thumb means is the store's decision, not this component's. */
  readonly rated = output<MessageFeedbackRating>();

  /**
   * Tests root membership rather than `parentScopeId`, so an orphan the fold promoted stays
   * visible. Roots are consumed from per-id queues: missing ids default to '', so a plain lookup
   * could render one twice, and a single pointer would drop every card after a mismatch.
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
        // Per node: an email interrupted by a tool call is a closed fence in neither half,
        // so it stays markdown.
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

  /** Null for any turn that never folded a `Finished`: stopped, cut off, or replayed. */
  protected readonly usageLine = computed(() => formatTurnUsage(this.snapshot().usage));

  /**
   * The original markdown, read off the snapshot rather than re-joined from
   * {@link renderedNodes}, whose slices are not guaranteed to cover the whole text.
   */
  protected readonly copySource = computed(() => this.snapshot().text ?? '');

  /**
   * For an email the model never marked; null once a card carries its own control. Fixed for a
   * settled entry, so its `@if` cannot toggle under the transcript's ResizeObserver.
   */
  protected readonly footerDraft = computed(() => {
    const hasCard = this.renderedNodes().some(
      (node) => node.kind === 'text' && node.segments.some((part) => part.kind === 'email'),
    );

    return hasCard ? null : detectEmailDraft(this.copySource());
  });

  /** The live region's own `aria-busy`, which is better honoured than the transcript's. */
  protected readonly liveAnswer = computed(
    () => this.streaming() && this.snapshot().phase !== 'Completed',
  );

  /** The caret belongs to the last node, and only when it is a text block. */
  protected readonly caretIndex = computed(() => {
    const nodes = this.renderedNodes();
    const last = nodes[nodes.length - 1];

    return this.streaming() && last !== undefined && last.kind === 'text' ? nodes.length - 1 : -1;
  });

  /** `head` stays byte-identical until a new block closes, so only the tail is re-parsed. */
  private readonly liveSplit = computed<StreamSplit>(() => {
    const index = this.caretIndex();
    const last = index === -1 ? undefined : this.renderedNodes()[index];

    return last?.kind === 'text' ? splitStreamingMarkdown(last.text) : { head: '', tail: '' };
  });

  protected readonly liveHead = computed(() => this.liveSplit().head);

  /** Without fence info, so a half-written code block is not highlighted against its grammar. */
  protected readonly liveTail = computed(() => stripFenceInfo(this.liveSplit().tail));
}
