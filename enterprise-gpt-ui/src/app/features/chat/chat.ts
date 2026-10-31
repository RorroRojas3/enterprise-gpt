import { NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  Injector,
  TemplateRef,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { Events } from '@ngrx/signals/events';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { ConversationDownloadMenu } from '@core/conversations/conversation-download-menu';
import { ConversationListStore } from '@core/conversations/conversation-list-store';
import { conversationEvents } from '@core/events/conversation-events';
import { ShellChrome } from '@core/layout/shell-chrome';
import { ProjectPicker } from '@core/projects/project-picker';
import { Icon } from '@shared/icons/icon';
import { MOBILE_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { Skeleton } from '@shared/loading/skeleton';
import { Menu } from '@shared/menu/menu';
import { MenuItem } from '@shared/menu/menu-item';
import { MenuSeparator } from '@shared/menu/menu-separator';
import { conversationOf, exportTargetOf } from './chat-composer-host';
import { ChatSessionView } from './chat-session-view';
import { ChatSessions } from './chat-sessions';
import { ConversationStore } from './conversation-store';
import { provideChatMarkdown } from './markdown/markdown-providers';

/** One route serves `/chat` and `/chat/{id}`, so switching conversations never remounts this. */
@Component({
  selector: 'app-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConversationDownloadMenu,
    Icon,
    Menu,
    MenuItem,
    MenuSeparator,
    NgComponentOutlet,
    NgTemplateOutlet,
    ProjectPicker,
    Skeleton,
  ],
  // Markdown is provided here so ngx-markdown, marked and Prism ride the lazy chat chunk.
  providers: [ConversationStore, provideChatMarkdown()],
  templateUrl: './chat.html',
  styleUrl: './chat.scss',
  host: { class: 'd-flex flex-column flex-fill min-h-0' },
})
export class Chat {
  /** Goes `undefined` on `/chat`, which is how a conversation closes without a remount. */
  readonly conversationId = input<string>();

  protected readonly conversation = inject(ConversationStore);
  protected readonly actions = inject(ConversationActionsStore);
  protected readonly sessions = inject(ChatSessions);
  private readonly list = inject(ConversationListStore);

  protected readonly sessionView = ChatSessionView;

  /** The header's download menu; the composer's own copy lives on its host. */
  protected readonly exportTarget = computed(() => {
    const turn = this.sessions.active()?.turn;

    return turn === undefined
      ? null
      : exportTargetOf(
          conversationOf(
            turn.boundConversationId(),
            this.list.entityMap(),
            this.conversation.conversation(),
          ),
          turn.entries().length > 0,
        );
  });

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

  constructor() {
    // Called here so the reactive methods bind to this component, not the root injector. The id
    // the first-prompt create already bound finds its own session, so nothing is read back.
    this.conversation.open(this.conversationId);
    this.sessions.activate(this.conversationId);
    inject(DestroyRef).onDestroy(() => this.sessions.deactivate());

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
