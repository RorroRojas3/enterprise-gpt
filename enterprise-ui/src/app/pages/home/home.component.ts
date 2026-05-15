import { Component, computed, DestroyRef, inject, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PromptBoxComponent } from '../../components/home/prompt-box/prompt-box.component';
import { StoreService } from '../../store/store.service';
import { MessageBubbleComponent } from '../../components/home/message-bubble/message-bubble.component';
import { MessageDto, createMessage } from '../../dtos/MessageDto';
import { ConversationMessageDto } from '../../dtos/ConversationDto';
import markdownit from 'markdown-it';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import markdown_it_highlightjs from 'markdown-it-highlightjs';
import hljs from 'highlight.js';
import { ActivatedRoute } from '@angular/router';
import { ConversationService } from '../../services/conversation.service';
import { NotificationService } from '../../services/notification.service';
import { catchError, EMPTY, switchMap, tap } from 'rxjs';
import { ChatRoles } from '../../dtos/const/ChatRoles';

@Component({
  selector: 'app-home',
  imports: [PromptBoxComponent, MessageBubbleComponent],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
})
export class HomeComponent implements OnInit {
  public readonly storeService = inject(StoreService);
  private readonly conversationService = inject(ConversationService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notificationService = inject(NotificationService);

  private readonly md: markdownit;

  canHighlight = computed(() => {
    return this.storeService.isStreaming();
  });

  constructor() {
    this.md = new markdownit({
      html: true,
      linkify: true,
      typographer: true,
    }).use(markdown_it_highlightjs, { hljs });
  }

  ngOnInit(): void {
    // Listen to route parameter changes to load conversation when conversationId changes
    this.route.params
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((params) => {
        const conversationId = params['conversationId'];
        if (conversationId) {
          this.loadConversation(conversationId);
        }
      });
  }

  /**
   * Loads the conversation for a given conversation ID.
   *
   * This method performs the following steps:
   * 1. Fetches conversation information from the API
   * 2. Updates the store with the conversation data
   * 3. Retrieves the messages for the conversation
   * 4. Processes messages, converting assistant messages from markdown to sanitized HTML
   * 5. Updates the store with the processed messages
   *
   * If any step fails, displays an error notification and resets to a new conversation.
   * Properly manages subscriptions to prevent memory leaks using takeUntilDestroyed.
   *
   * @param conversationId - The unique identifier of the conversation to load
   */
  loadConversation(conversationId: string): void {
    this.conversationService
      .getConversation(conversationId)
      .pipe(
        tap((conv) => {
          // Step 2: Set the conversation in the store
          this.storeService.conversation.set(conv);
        }),
        switchMap((conv) => {
          // Step 3: Get the conversation messages after successful conversation fetch
          return this.conversationService.getConversationMessages(conv.id);
        }),
        catchError(() => {
          // Step 6: Catch errors and display notification
          this.notificationService.error('Error loading chat conversation.');
          this.storeService.clearForNewConversation();
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef), // Step 4: Prevent memory leaks
      )
      .subscribe((response) => {
        // Step 4: Process and update messages
        const mappedMessages = response.messages.map(
          (message: ConversationMessageDto) => {
            if (message.role === ChatRoles.ASSISTANT) {
              const html = this.md.render(message.text);
              const sanitizeHtml = this.sanitizer.bypassSecurityTrustHtml(html);
              return createMessage(message.text, false, sanitizeHtml);
            }
            return createMessage(message.text, true, undefined);
          },
        );
        this.storeService.messages.set(mappedMessages);
      });
  }

  /**
   * Handles changes to the markdown content by synchronizing the change
   * with the stream message in the store service.
   *
   * @param markdown - The updated markdown content as SafeHtml
   */
  onMarkdownChange(markdown: SafeHtml): void {
    this.storeService.streamMessage.update((streamMessage) => ({
      ...streamMessage,
      markdown,
    }));
  }

  /**
   * TrackBy function for message list to improve performance
   */
  trackByMessage(index: number, _message: MessageDto): number {
    return index;
  }
}
