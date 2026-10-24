import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  Injector,
  TemplateRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { Events } from '@ngrx/signals/events';
import { MOBILE_VIEWPORT } from '@shared/utils/layout/breakpoints';
import { provideComposerHost } from '@core/chat/composer-host.token';
import { canRetry } from '@core/errors/error-message';
import { conversationEvents } from '@core/events/conversation-events';
import { injectMediaQuery } from '@core/ui/media-query';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { ConversationStore } from '@state/chat/conversation-store';
import { TurnStore } from '@state/chat/turn-store';
import { ConversationActionsStore } from '@state/conversation-actions-store';
import { provideSharedComposerUploads } from '@state/documents/composer-uploads.token';
import { UploadStore } from '@state/documents/upload-store';
import { ConversationDownloadMenu } from '@shared/components/conversation-download-menu/conversation-download-menu';
import { ErrorPanel } from '@shared/components/error-panel/error-panel';
import { Icon } from '@shared/components/icon/icon';
import { Menu } from '@shared/components/menu/menu';
import { ProjectPicker } from '@shared/components/project-picker/project-picker';
import { Skeleton } from '@shared/components/skeleton/skeleton';
import { MenuItem } from '@shared/directives/menu-item';
import { MenuSeparator } from '@shared/directives/menu-separator';
import { ChatEmptyState } from '@components/chat/chat-empty-state';
import { provideChatMarkdown } from '@components/chat/markdown/markdown-providers';
import { TranscriptPinning } from '@components/chat/transcript-pinning';
import { Transcript } from '@components/chat/transcript/transcript';
import { Composer } from '@components/composer/composer';

/** One route serves `/chat` and `/chat/{id}`, so switching conversations never remounts this. */
@Component({
  selector: 'app-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ChatEmptyState,
    Composer,
    ConversationDownloadMenu,
    ErrorPanel,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    NgTemplateOutlet,
    ProjectPicker,
    Skeleton,
    Transcript,
    TranscriptPinning,
  ],
  // Markdown is provided here so ngx-markdown, marked and Prism ride the lazy chat chunk.
  // `provideComposerHost` streams in place here; the project screen's host navigates instead.
  providers: [
    ConversationStore,
    UploadStore,
    provideSharedComposerUploads(),
    TurnStore,
    provideComposerHost(TurnStore),
    provideChatMarkdown(),
  ],
  templateUrl: './chat.html',
  styleUrl: './chat.scss',
  host: { class: 'd-flex flex-column flex-fill min-h-0' },
})
export class Chat {
  /** Goes `undefined` on `/chat`, which is how a conversation closes without a remount. */
  readonly conversationId = input<string>();

  protected readonly conversation = inject(ConversationStore);
  protected readonly actions = inject(ConversationActionsStore);
  protected readonly turn = inject(TurnStore);
  private readonly uploads = inject(UploadStore);

  /**
   * The star and kebab, rendered in the header at desktop and in the shell-owned navbar (via
   * {@link ShellChrome}) on mobile.
   */
  private readonly navbarActions = viewChild<TemplateRef<unknown>>('navbarActions');

  /** Picks one host: rendering the TemplateRef twice would put two menus in the DOM. */
  protected readonly isNarrow = injectMediaQuery(MOBILE_VIEWPORT);

  /** Below md the shell's navbar is the bar, so the header keeps only its hidden heading. */
  protected readonly headerClass = computed(() =>
    this.isNarrow()
      ? ''
      : 'chat__header d-flex align-items-center gap-3 px-4 border-bottom flex-shrink-0',
  );

  /** The project picker, opened from the header kebab and anchored where the kebab was. */
  protected readonly pickerOpen = signal(false);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /** A 404 (missing or someone else's) never succeeds on retry, so the panel offers none. */
  protected readonly canRetry = canRetry;

  constructor() {
    // Called here so the reactive method binds to this component, not the root injector.
    this.conversation.open(this.conversationId);
    // The id the first-prompt create already bound passes through as a no-op, so a
    // conversation whose first turn is still streaming is not read back.
    this.turn.bindRoute(this.conversationId);

    // Changing conversation drops the chips. The first-prompt create also binds the id
    // synchronously, so a deferred upload starts without waiting for change detection.
    this.uploads.bindConversation(this.conversationId);
    // Attaching states intent and Send is the instruction; `TurnStore` posts them at send time.
    this.uploads.deferUploadsUntilSend();

    const router = inject(Router);
    const document = inject(DOCUMENT);
    const injector = inject(Injector);

    // The open conversation was deleted. The event fires on the 204, never optimistically,
    // and a user who moved elsewhere mid-flight is left alone.
    inject(Events)
      .on(conversationEvents.deleted)
      .pipe(takeUntilDestroyed())
      .subscribe(({ payload: id }) => {
        if (id !== this.conversationId()) {
          return;
        }

        void router.navigateByUrl('/chat').then(() => {
          // The focused header kebab is removed only on the next render, after this promise
          // resolves. Only a fall-through is corrected; focus elsewhere is left alone.
          afterNextRender(
            () => {
              const active = document.activeElement;
              if (active === null || active === document.body || !active.isConnected) {
                document.getElementById('main-content')?.focus();
              }
            },
            { injector },
          );
        });
      });

    // A ready chip would make every later prompt look attached; retrieval reaches it anyway.
    // Waits for the turn so a chip is not pulled from under the note explaining Send's wait.
    effect(() => {
      if (this.turn.inFlight()) {
        return;
      }

      const settled = this.uploads.attachments().filter((row) => row.state.kind === 'ready');
      if (settled.length === 0) {
        return;
      }

      untracked(() => {
        for (const row of settled) {
          this.uploads.remove(row.id);
        }
      });
    });

    // Optional so specs can mount this without a shell. Published once, as the TemplateRef
    // never changes; cleared on destroy so the kebab does not appear over the next screen.
    const chrome = inject(ShellChrome, { optional: true });
    if (chrome !== null) {
      afterNextRender(
        () => {
          const actions = this.navbarActions();
          if (actions !== undefined) {
            chrome.publish({ actions });
          }
        },
        { injector },
      );
      inject(DestroyRef).onDestroy(() => chrome.clear());
    }
  }

  /**
   * Captures the anchor at the gesture: the menu closes on this click, and a template binding
   * would read the trigger before the menu's view exists.
   */
  protected openPicker(menu: Menu): void {
    if (this.conversation.actionPending()) {
      return;
    }

    this.pickerAnchor.set(menu.triggerElement());
    this.pickerOpen.set(true);
  }
}
