import {
  Component,
  DestroyRef,
  EventEmitter,
  inject,
  Input,
  OnInit,
  Output,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { StoreService } from '../../store/store.service';
import { ConversationService } from '../../services/conversation.service';
import { FormsModule } from '@angular/forms';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  EMPTY,
  finalize,
  forkJoin,
  of,
  Subject,
  switchMap,
} from 'rxjs';

import { Router } from '@angular/router';
import { ConversationDto } from '../../dtos/ConversationDto';
import { RenameModalComponent } from '../../pages/conversations/components/rename-modal/rename-modal.component';
import { UpdateConversationActionDto } from '../../dtos/actions/conversation/UpdateConversationActionDto';
import { NotificationService } from '../../services/notification.service';
import { DeleteModalComponent } from '../../pages/conversations/components/delete-modal/delete-modal.component';
import { MsalService } from '@azure/msal-angular';
@Component({
  selector: 'app-menu-offcanvas',
  imports: [FormsModule, RenameModalComponent, DeleteModalComponent],
  templateUrl: './menu-offcanvas.component.html',
  styleUrl: './menu-offcanvas.component.scss',
})
export class MenuOffcanvasComponent implements OnInit {
  // Constants
  readonly MAX_CONVERSATION_NAME_LENGTH = 40;
  readonly SEARCH_DEBOUNCE_MS = 600;

  // Inputs/Outputs for mobile sidebar
  @Input() mobileOpen = false;
  @Output() mobileOpenChange = new EventEmitter<boolean>();

  // Inject dependencies
  public readonly storeService = inject(StoreService);
  private readonly conversationService = inject(ConversationService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notificationService = inject(NotificationService);
  private readonly authService = inject(MsalService);

  private searchSubject = new Subject<string>();
  showRenameModal = false;
  showDeleteModal = false;

  // Sidebar collapse state (desktop)
  sidebarCollapsed = false;

  // User info
  userName = '';
  userInitials = '';

  ngOnInit(): void {
    // Restore sidebar collapsed state
    this.sidebarCollapsed =
      localStorage.getItem('sidebarCollapsed') === 'true';

    // Get user info from MSAL
    const account = this.authService.instance.getActiveAccount();
    if (account?.name) {
      this.userName = account.name;
      this.userInitials = account.name
        .split(' ')
        .map((n) => n.charAt(0))
        .join('')
        .substring(0, 2)
        .toUpperCase();
    }

    // Set up debounced search with automatic cleanup
    this.searchSubject
      .pipe(
        debounceTime(this.SEARCH_DEBOUNCE_MS),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((filter: string) => {
        this.performSearch(filter);
      });
  }

  toggleCollapse(): void {
    if (window.innerWidth < 768) {
      // Mobile: close overlay
      this.closeMobileSidebar();
      return;
    }
    if (window.innerWidth < 1024) {
      // Tablet: no toggle
      return;
    }
    this.sidebarCollapsed = !this.sidebarCollapsed;
    localStorage.setItem('sidebarCollapsed', String(this.sidebarCollapsed));
  }

  closeMobileSidebar(): void {
    this.mobileOpen = false;
    this.mobileOpenChange.emit(false);
  }

  /**
   * Performs search using the conversation service
   */
  private performSearch(filter: string): void {
    this.storeService.setMenuConversationSearchFilter(filter);
    this.conversationService
      .loadMenuConversations()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  trackByConversationId(
    _index: number,
    conversation: ConversationDto,
  ): string {
    return conversation.id;
  }

  getTruncatedConversationName(conversation: ConversationDto): string {
    return conversation.name.length > this.MAX_CONVERSATION_NAME_LENGTH
      ? conversation.name.slice(0, this.MAX_CONVERSATION_NAME_LENGTH) + '...'
      : conversation.name;
  }

  isCurrentConversation(conversationId: string): boolean {
    return this.storeService.conversation()?.id === conversationId;
  }

  onClickConversation(conversationId: string): void {
    const foundConversation = this.storeService
      .menuConversations()
      .find((c) => c.id === conversationId);
    if (foundConversation) {
      this.storeService.conversation.set(foundConversation);
      this.router.navigate(['conversation', foundConversation.id]);
    }
    this.closeMobileSidebar();
  }

  onClickCreateNewConversation(): void {
    this.storeService.clearForNewConversation();
    this.router.navigate(['conversation']);
    this.closeMobileSidebar();
  }

  onSearchChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.searchSubject.next(target.value);
  }

  clearSearch(): void {
    this.storeService.clearMenuConversationSearchFilter();
    this.conversationService
      .loadMenuConversations()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe();
  }

  onClickGoToChats(): void {
    this.storeService.clearForNewConversation();
    this.router.navigate(['conversations']);
    this.closeMobileSidebar();
  }

  onClickGoToProjects(): void {
    this.storeService.clearForNewConversation();
    this.router.navigate(['projects']);
    this.closeMobileSidebar();
  }

  onRenameConversation(conversationId: string): void {
    const conversation = this.storeService
      .menuConversations()
      .find((c) => c.id === conversationId);
    if (conversation) {
      this.storeService.conversation.set(conversation);
      this.showRenameModal = true;
    }
  }

  handleRenameConversation(newName: string): void {
    const currentConversation = this.storeService.conversation();
    if (!currentConversation) {
      this.onCloseRenameModal();
      return;
    }

    const request = new UpdateConversationActionDto(
      currentConversation.id,
      newName,
    );

    this.conversationService
      .updateConversation(request)
      .pipe(
        catchError(() => {
          this.notificationService.error('Error renaming conversation.');
          return EMPTY;
        }),
        switchMap((response) => {
          this.storeService.conversation.set(response);
          this.notificationService.success('Conversation renamed successfully.');

          const shouldReloadPageConversations = this.storeService
            .pageConversations()
            .some((c) => c.id === response.id);

          return forkJoin({
            menuConversations:
              this.conversationService.loadMenuConversations(),
            pageConversations: shouldReloadPageConversations
              ? this.conversationService.loadPageConversations(
                  this.storeService.pageConversationSearchFilter(),
                  0,
                  this.storeService.CONVERSATION_PAGE_SIZE,
                )
              : of(undefined),
          });
        }),
        finalize(() => {
          this.onCloseRenameModal();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  onCloseRenameModal(): void {
    this.showRenameModal = false;
  }

  onDeleteConversation(conversationId: string): void {
    const conversation = this.storeService
      .menuConversations()
      .find((c) => c.id === conversationId);
    if (conversation) {
      this.storeService.conversation.set(conversation);
      this.showDeleteModal = true;
    }
  }

  handleDeleteConversation(): void {
    const currentConversation = this.storeService.conversation();
    if (!currentConversation) {
      this.onCloseDeleteModal();
      return;
    }

    const conversationId = currentConversation.id;
    this.conversationService
      .deactivateConversation(conversationId)
      .pipe(
        catchError(() => {
          this.notificationService.error('Error deleting conversation.');
          return EMPTY;
        }),
        switchMap(() => {
          this.storeService.conversation.set(null);
          this.notificationService.success('Conversation deleted successfully.');

          const shouldReloadPageConversations = this.storeService
            .pageConversations()
            .some((c) => c.id === conversationId);

          return forkJoin({
            menuConversations:
              this.conversationService.loadMenuConversations(),
            pageConversations: shouldReloadPageConversations
              ? this.conversationService.loadPageConversations(
                  this.storeService.pageConversationSearchFilter(),
                  0,
                  this.storeService.CONVERSATION_PAGE_SIZE,
                )
              : of(undefined),
          });
        }),
        switchMap(() => {
          this.onClickCreateNewConversation();
          return of(undefined);
        }),
        finalize(() => {
          this.onCloseDeleteModal();
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  onCloseDeleteModal(): void {
    this.showDeleteModal = false;
  }
}
