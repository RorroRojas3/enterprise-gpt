import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  Injector,
  TemplateRef,
  afterNextRender,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { Events } from '@ngrx/signals/events';
import { provideComposerHost } from '@core/chat/composer-host';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { canRetry } from '@core/errors/error-message';
import { ShellChrome } from '@core/ui/shell-chrome';
import { conversationEvents } from '@core/events/conversation-events';
import { ErrorPanel } from '@shared/feedback/error-panel/error-panel';
import { Skeleton } from '@shared/feedback/skeleton/skeleton';
import { Icon } from '@shared/icon/icon';
import { MOBILE_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';
import { MenuSeparator } from '@shared/overlay/menu/menu-separator';
import { ProjectPicker } from '@shared/projects/project-picker/project-picker';
import { ConversationDownloadMenu } from '@shared/conversations/conversation-download-menu/conversation-download-menu';
import { ChatEmptyState } from './chat-empty-state';
import { Composer } from '@shared/composer/composer';
import { ConversationStore } from './conversation-store';
import { provideChatMarkdown } from './markdown/markdown-providers';
import { TranscriptPinning } from './transcript-pinning';
import { Transcript } from './transcript/transcript';
import { TurnStore } from './turn-store';
import { provideSharedComposerUploads } from '@core/documents/composer-uploads';
import { UploadStore } from '@core/documents/upload-store';

/**
 * The chat screen, served for both `/chat` and `/chat/{conversationId}` by the single
 * `chatMatcher` route — so moving between them changes a parameter rather than
 * remounting the page, which is what lets US-401 create a conversation mid-turn
 * without losing the stream.
 *
 * An open conversation shows its 52px header (frame `1b`); the header is **absent**
 * rather than empty when no conversation is open, which is frame `1a`. The body is the
 * transcript once any turn state exists, and the empty state otherwise.
 */
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
  // US-601: the markdown renderer is provided here, at the lazy route's own
  // component, so ngx-markdown/marked/Prism ride the chat chunk instead of the
  // initial bundle.
  // UploadStore is listed before TurnStore only for readability; TurnStore injects it,
  // and Angular resolves both from this injector regardless of order.
  //
  // `provideComposerHost` is what makes the shared composer stream in place here: on
  // the project detail screen the same component is pointed at a host that creates a
  // conversation and navigates instead (US-906).
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
})
export class Chat {
  /**
   * Bound from the route by `withComponentInputBinding()`, and set back to `undefined`
   * when the parameter disappears — which is how navigating from `/chat/{id}` to
   * `/chat` closes the conversation without a remount.
   */
  readonly conversationId = input<string>();

  protected readonly conversation = inject(ConversationStore);
  protected readonly actions = inject(ConversationActionsStore);
  protected readonly turn = inject(TurnStore);
  private readonly uploads = inject(UploadStore);

  /**
   * The star and the kebab, declared once and rendered at one of two places: the 52px
   * header at desktop, and — through {@link ShellChrome} — frame `1d`'s navbar below
   * 768px, which the shell owns because the hamburger beside them opens the sidebar.
   */
  private readonly navbarActions = viewChild<TemplateRef<unknown>>('navbarActions');

  /**
   * Which of the two renders it. Exactly one does at any width, because a `TemplateRef`
   * instantiated twice would put two of this menu — and two project pickers — in the
   * document at once.
   */
  protected readonly isNarrow = injectMediaQuery(MOBILE_VIEWPORT);

  /** Frame `2e`'s panel, opened from the header kebab and anchored where the kebab was. */
  protected readonly pickerOpen = signal(false);
  protected readonly pickerAnchor = signal<HTMLElement | null>(null);

  /**
   * A 404 here means the conversation does not exist *or* belongs to someone else, and
   * neither starts being true on a second attempt — so the panel offers no Retry.
   */
  protected readonly canRetry = canRetry;

  constructor() {
    // The constructor is an injection context, which a signal-valued reactive method
    // requires: it binds the subscription to this component rather than to the root
    // injector, where it would outlive the screen.
    this.conversation.open(this.conversationId);
    // The turn store follows the same input: a conversation change aborts and
    // clears the session turn state (US-407) and replays the stored messages
    // (US-410), while the id US-401's own create already bound passes through
    // as a no-op — which is what keeps it from reading back a conversation
    // whose first turn is still streaming.
    this.turn.bindRoute(this.conversationId);

    // The route is the authority on which conversation the chips belong to, so moving
    // to another one drops them. A `signalMethod`, exactly as `bindRoute` above is:
    // it is not the *only* caller, because US-401's create binds the id synchronously
    // the moment the conversation exists so a deferred upload starts without waiting
    // for a change-detection pass — and the same method takes a bare value there.
    this.uploads.bindConversation(this.conversationId);
    // The composer's rule, not a file manager's: attaching states an intention and Send is
    // the instruction. `TurnStore` posts them at the head of the send pipeline.
    this.uploads.deferUploadsUntilSend();

    const router = inject(Router);
    const document = inject(DOCUMENT);
    const injector = inject(Injector);

    // US-306: the open conversation was deleted. The event fires only on the 204,
    // so this navigates when the delete *completes*, never optimistically — and the
    // `conversationId` input is the authoritative "is this the one open" fact, so a
    // user who moved to another conversation mid-flight is left alone. `chatMatcher`
    // serves both URLs, so this is an input change, not a remount.
    inject(Events)
      .on(conversationEvents.deleted)
      .pipe(takeUntilDestroyed())
      .subscribe(({ payload: id }) => {
        if (id !== this.conversationId()) {
          return;
        }

        void router.navigateByUrl('/chat').then(() => {
          // The navigation unmounts whatever held focus — the header kebab (US-308)
          // — but only on the *next render*: the promise resolves in a microtask,
          // before zoneless change detection has removed the header, so an
          // immediate check would still find the trigger connected and skip the
          // fixup. afterNextRender waits that render out. Only a fall-through is
          // corrected; a user focused elsewhere is not yanked.
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

    // A finished chip has done its job: the document is the conversation's, the next turn
    // reaches it through retrieval, and leaving it on screen makes every later prompt
    // look like it carries an attachment. Only once the turn is done, so a chip is not
    // pulled out from under the note line explaining why Send is waiting on it.
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

    // The navbar slot. `optional`, because every route spec instantiates this component
    // without a shell around it, and a required dependency would make this seam a reason
    // to rewrite them all.
    //
    // Published once rather than from an effect: the template reference is fixed for the
    // life of the component, and only *where* it is rendered changes with the viewport.
    // Cleared on destroy, or the conversation kebab outlives the conversation and
    // appears over the next screen.
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
   * Opens the project picker where the header kebab is (US-307).
   *
   * The anchor is captured at the gesture for the reason the sidebar row's is: the menu
   * closes on this same click, and a template binding would read its trigger on the
   * first change-detection pass, before the menu's view exists.
   */
  protected openPicker(menu: Menu): void {
    if (this.conversation.actionPending()) {
      return;
    }

    this.pickerAnchor.set(menu.triggerElement());
    this.pickerOpen.set(true);
  }
}
