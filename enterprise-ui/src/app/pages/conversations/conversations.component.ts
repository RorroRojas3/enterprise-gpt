import { Component, OnInit, DestroyRef, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ConversationService } from '../../services/conversation.service';
import { ConversationDto } from '../../dtos/ConversationDto';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  EMPTY,
  finalize,
  forkJoin,
  Subject,
  switchMap,
  tap,
} from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { StoreService } from '../../store/store.service';
import { DeleteModalComponent } from './components/delete-modal/delete-modal.component';
import { DeactivateConversationBulkActionDto } from '../../dtos/actions/conversation/DeactivateConversationBulkActionDto';
import { RenameModalComponent } from './components/rename-modal/rename-modal.component';
import { UpdateConversationActionDto } from '../../dtos/actions/conversation/UpdateConversationActionDto';
import { NotificationService } from '../../services/notification.service';

@Component({
  selector: 'app-conversations',
  imports: [
    CommonModule,
    FormsModule,
    DeleteModalComponent,
    RenameModalComponent,
  ],
  templateUrl: './conversations.component.html',
  styleUrl: './conversations.component.scss',
})
export class ConversationsComponent implements OnInit {
  // Constants
  readonly SEARCH_DEBOUNCE_MS = 600;

  // Inject dependencies using Angular 19 pattern
  public readonly storeService = inject(StoreService);
  private readonly conversationService = inject(ConversationService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notificationService = inject(NotificationService);

  isLoadingMore = false;

  // Modal visibility
  showDeleteModal = false;
  selectedConversationIds: string[] = [];
  showRenameModal = false;
  conversationName = '';

  // Checkbox selection tracking
  hoveredConversationId: string | null = null;

  private searchSubject = new Subject<string>();

  ngOnInit(): void {
    this.storeService.setPageConversationSearchFilter('');

    // Set up debounced search with switchMap to avoid race conditions
    this.searchSubject
      .pipe(
        debounceTime(this.SEARCH_DEBOUNCE_MS),
        distinctUntilChanged(),
        switchMap((filter) => {
          this.conversationService.clearPageConversations();
          return this.conversationService.loadPageConversations(
            filter,
            this.storeService.pageConversationSkip(),
            this.storeService.CONVERSATION_PAGE_SIZE,
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();

    // Load initial conversations
    this.conversationService
      .loadPageConversations(
        this.storeService.pageConversationSearchFilter(),
        0,
        this.storeService.CONVERSATION_PAGE_SIZE,
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  /**
   * Handles search input changes
   */
  onSearchChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.searchSubject.next(target.value);
  }

  /**
   * Clears the search term and reloads conversations
   */
  clearSearch(): void {
    this.conversationService
      .loadPageConversations('', 0, this.storeService.CONVERSATION_PAGE_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  /**
   * TrackBy function for conversation list to improve performance
   */
  trackByConversationId(_index: number, conversation: ConversationDto): string {
    return conversation.id;
  }

  /**
   * Loads more conversations (next page) and appends them to the existing list
   */
  loadMoreConversations(): void {
    if (!this.storeService.pageConversationHasMore() || this.isLoadingMore) {
      return;
    }

    this.isLoadingMore = true;
    this.storeService.setPageConversationSkip(
      this.storeService.pageConversationSkip() + this.storeService.CONVERSATION_PAGE_SIZE,
    );

    this.conversationService
      .searchConversations(
        this.storeService.pageConversationSearchFilter(),
        this.storeService.pageConversationSkip(),
        this.storeService.CONVERSATION_PAGE_SIZE,
      )
      .pipe(
        catchError(() => {
          this.notificationService.error('Error loading chats.');
          return EMPTY;
        }),
        finalize(() => (this.isLoadingMore = false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((response) => {
        this.conversationService.handlePageConversationsResponse(
          response.items,
          response.totalCount,
          true,
        );
      });
  }

  /**
   * Navigates to the selected conversation
   */
  onClickConversation(conversationId: string): void {
    this.router.navigate(['conversation', conversationId]);
  }

  onClickCreateNewConversation(): void {
    this.storeService.clearForNewConversation();
    this.router.navigate(['conversation']);
  }

  /**
   * Toggles the selection state of a conversation checkbox
   */
  toggleConversationSelection(conversationId: string, event: Event): void {
    event.stopPropagation(); // Prevent navigation when clicking checkbox

    const index = this.selectedConversationIds.indexOf(conversationId);
    if (index > -1) {
      this.selectedConversationIds.splice(index, 1);
    } else {
      this.selectedConversationIds.push(conversationId);
    }
  }

  /**
   * Checks if a conversation is selected
   */
  isConversationSelected(conversationId: string): boolean {
    return this.selectedConversationIds.includes(conversationId);
  }

  /**
   * Checks if checkbox should be visible for a conversation
   */
  shouldShowCheckbox(conversationId: string): boolean {
    return (
      this.hoveredConversationId === conversationId || this.selectedConversationIds.length > 0
    );
  }

  /**
   * Handles mouse enter on a conversation item
   */
  onConversationMouseEnter(conversationId: string): void {
    this.hoveredConversationId = conversationId;
  }

  /**
   * Handles mouse leave on a conversation item
   */
  onConversationMouseLeave(): void {
    this.hoveredConversationId = null;
  }

  /**
   * Deselects all selected conversations
   */
  deselectAllConversations(): void {
    this.selectedConversationIds = [];
  }

  /**
   * Initiates deletion of selected conversations
   */
  onDeleteSelectedConversations(): void {
    if (this.selectedConversationIds.length === 0) {
      return;
    }
    this.showDeleteModal = true;
  }

  /**
   * Initiates the delete process for a specific conversation.
   *
   * Selects the specified conversation and opens the delete confirmation modal.
   *
   * @param {string} conversationId - The unique identifier of the conversation to delete
   * @returns {void}
   */
  onDeleteConversation(conversationId: string): void {
    this.selectedConversationIds = [conversationId];
    this.showDeleteModal = true;
  }

  /**
   * Handles the delete conversation operation by calling appropriate endpoints.
   *
   * Determines whether to use single or bulk delete endpoint based on the
   * number of selected conversations. Validates that at least one conversation is
   * selected, calls the appropriate service method, and handles errors
   * with notifications. Reloads conversations on success.
   *
   * @returns {void}
   */
  handleDeleteConversation(): void {
    if (this.selectedConversationIds.length === 0) {
      this.closeDeleteModal();
      return;
    }

    // If single conversation, use single delete endpoint
    if (this.selectedConversationIds.length === 1) {
      const conversationId = this.selectedConversationIds[0];
      this.conversationService
        .deactivateConversation(conversationId)
        .pipe(
          catchError(() => {
            this.notificationService.error('Error deleting chat.');
            this.closeDeleteModal();
            return EMPTY;
          }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe(() => {
          this.handleDeleteSuccess();
        });
    } else {
      // Multiple conversations, use bulk delete endpoint
      const bulkRequest = new DeactivateConversationBulkActionDto();
      bulkRequest.conversationIds = [...this.selectedConversationIds];

      this.conversationService
        .deactivateConversationBulk(bulkRequest)
        .pipe(
          catchError(() => {
            this.notificationService.error('Error deleting chats.');
            this.closeDeleteModal();
            return EMPTY;
          }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe(() => {
          this.handleDeleteSuccess();
        });
    }
  }

  /**
   * Handles successful deletion by reloading conversations and updating store
   */
  private handleDeleteSuccess(): void {
    this.closeDeleteModal();
    forkJoin({
      pageConversations: this.conversationService.loadPageConversations(
        this.storeService.pageConversationSearchFilter(),
        0,
        this.storeService.CONVERSATION_PAGE_SIZE,
      ),
      menuConversations: this.conversationService.loadMenuConversations(),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  /**
   * Closes the delete modal and resets the selection state.
   *
   * This method hides the delete confirmation modal dialog and clears
   * the array of selected conversation IDs.
   *
   * @returns {void}
   */
  closeDeleteModal(): void {
    this.showDeleteModal = false;
    this.selectedConversationIds = [];
  }

  /**
   * Initiates the rename process for a specific conversation.
   *
   * Finds the conversation by ID in the store and opens the rename modal
   * with the current conversation name pre-populated.
   *
   * @param {string} conversationId - The unique identifier of the conversation to rename
   * @returns {void}
   */
  onRenameConversation(conversationId: string): void {
    const conversation = this.storeService
      .pageConversations()
      .find((c) => c.id === conversationId);
    if (conversation) {
      this.selectedConversationIds = [conversationId];
      this.conversationName = conversation.name;
      this.showRenameModal = true;
    }
  }

  /**
   * Handles the rename conversation operation by updating the conversation name.
   *
   * Validates that exactly one conversation is selected, then calls the conversation
   * service to update the conversation name. Displays an error notification on
   * failure and reloads conversations on success.
   *
   * @param {string} newName - The new name for the conversation
   * @returns {void}
   */
  handleRenameConversation(newName: string): void {
    if (this.selectedConversationIds.length !== 1) {
      this.closeRenameModal();
      return;
    }

    const conversationId = this.selectedConversationIds[0];
    const request = new UpdateConversationActionDto(conversationId, newName);

    this.conversationService
      .updateConversation(request)
      .pipe(
        catchError(() => {
          this.notificationService.error('Error renaming chat.');
          this.closeRenameModal();
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.handleRenameSuccess();
      });
  }

  /**
   * Handles successful rename by reloading conversations and updating store
   */
  private handleRenameSuccess(): void {
    this.closeRenameModal();
    forkJoin({
      pageConversations: this.conversationService.loadPageConversations(
        this.storeService.pageConversationSearchFilter(),
        0,
        this.storeService.CONVERSATION_PAGE_SIZE,
      ),
      menuConversations: this.conversationService.loadMenuConversations(),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  /**
   * Closes the rename modal and resets the conversation-related state.
   *
   * This method hides the rename modal dialog, clears the conversation name input,
   * and empties the array of selected conversation IDs.
   *
   * @returns {void}
   */
  closeRenameModal(): void {
    this.showRenameModal = false;
    this.conversationName = '';
    this.selectedConversationIds = [];
  }
}
