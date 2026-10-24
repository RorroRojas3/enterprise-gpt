import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  output,
  viewChild,
} from '@angular/core';
import { AppError } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';
import { Icon } from '@shared/icons/icon';

/** What the card explains: a turn that ended early, or one that never started. */
export type TurnNotice =
  { readonly kind: 'cut-off' } | { readonly kind: 'error'; readonly error: AppError };

interface NoticeView {
  readonly tone: 'warn' | 'surface';
  /** `row` is the one-line form, for the busy warning, which is a single sentence. */
  readonly layout: 'stack' | 'row';
  readonly icon: 'bi-exclamation-triangle-fill' | 'bi-plug' | 'bi-hourglass-split';
  /** Colour only — the title's weight follows {@link NoticeView.layout}. */
  readonly titleTone: 'warn' | 'fail' | 'body';
  readonly title: string;
  readonly body: string | null;
  readonly trace: string | null;
}

/**
 * Arms without designed copy fall back to the generic warning rather than invented text. Retry
 * is the caller's decision, since only it knows whether it holds a re-sendable turn.
 */
@Component({
  selector: 'app-turn-notice-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './turn-notice-card.html',
  host: { class: 'd-block' },
})
export class TurnNoticeCard {
  readonly notice = input.required<TurnNotice>();
  readonly retryable = input<boolean>(false);
  readonly retried = output<void>();

  private readonly retryButton = viewChild<ElementRef<HTMLButtonElement>>('retry');

  /** Retrying destroys this card, so a fresh failure's card takes focus back through this. */
  focusRetry(): void {
    this.retryButton()?.nativeElement.focus();
  }

  /** The one-line form: the message takes the free space and Retry sits at the far right. */
  protected readonly isRow = computed(() => this.view().layout === 'row');

  protected readonly frameClass = computed(() => {
    const frame =
      this.view().tone === 'warn'
        ? 'alert alert-warning rounded-4 mb-0'
        : 'card card-body rounded-4';

    return this.isRow() ? `${frame} d-flex flex-row align-items-center gap-2` : frame;
  });

  protected readonly iconClass = computed(() =>
    this.view().titleTone === 'fail' ? 'text-danger' : 'text-warning',
  );

  /** A one-line notice is a sentence, not a heading, so it keeps body weight. */
  protected readonly titleClass = computed(() => {
    const view = this.view();
    const tone = TITLE_TONES[view.titleTone];

    return view.layout === 'row' ? tone : `${tone} fw-semibold`;
  });

  /** The alert's own colour dims the body; on the plain card it moves to the muted tone. */
  protected readonly mutedClass = computed(() =>
    this.view().tone === 'warn' ? '' : 'text-body-secondary',
  );

  protected readonly actionsClass = computed(() => (this.isRow() ? 'flex-shrink-0' : 'ms-4 mt-2'));

  protected readonly view = computed<NoticeView>(() => {
    const notice = this.notice();
    if (notice.kind === 'cut-off') {
      return {
        tone: 'warn',
        layout: 'stack',
        icon: 'bi-exclamation-triangle-fill',
        titleTone: 'warn',
        title: 'This response was cut off',
        body: 'The turn ended without a completion signal — the answer below may be incomplete.',
        trace: null,
      };
    }

    const error = notice.error;
    if (error.kind === 'mcp-server-unavailable') {
      return {
        tone: 'surface',
        layout: 'stack',
        icon: 'bi-plug',
        titleTone: 'fail',
        title: error.serverName
          ? `${error.serverName} is unavailable`
          : 'A tool server is unavailable',
        body: 'The tool server could not be reached. This turn produced no answer.',
        trace: traceLine(error),
      };
    }

    if (error.kind === 'conversation-busy') {
      // Not `userMessage`'s copy: beside its prompt the card can say "another response". No
      // trace: a second tab holding the lock is not a defect worth a correlation id.
      return {
        tone: 'warn',
        layout: 'row',
        icon: 'bi-hourglass-split',
        titleTone: 'body',
        title: 'Another response is still running in this conversation.',
        body: null,
        trace: null,
      };
    }

    return {
      tone: 'warn',
      layout: 'stack',
      icon: 'bi-exclamation-triangle-fill',
      titleTone: 'warn',
      title: userMessage(error),
      body: null,
      trace: traceLine(error),
    };
  });
}

const TITLE_TONES: Record<NoticeView['titleTone'], string> = {
  warn: 'text-warning-emphasis',
  fail: 'text-danger-emphasis',
  body: 'text-body',
};
