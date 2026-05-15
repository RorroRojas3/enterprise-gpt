import { Injectable, signal } from '@angular/core';
import { MessageDto, createMessage } from '../dtos/MessageDto';
import { ConversationDto } from '../dtos/ConversationDto';

@Injectable({
  providedIn: 'root',
})
export class StoreService {
  constructor() {}

  // CONSTANTS
  readonly CONVERSATION_PAGE_SIZE = 10;

  conversation = signal<ConversationDto | null>(null);
  isStreaming = signal<boolean>(false);
  showStreamLoader = signal<boolean>(false);
  stream = signal<string>('');
  messages = signal<MessageDto[]>([]);
  streamMessage = signal<MessageDto>(createMessage('', false, undefined));
  fileExtensions = signal<string[]>([]);
  projectId = signal<string | undefined>(undefined);

  // Search functionality
  menuConversations = signal<ConversationDto[]>([]);
  menuConversationSearchFilter = signal<string>('');
  menuIsConversationSearching = signal<boolean>(false);

  pageConversations = signal<ConversationDto[]>([]);
  pageConversationSearchFilter = signal<string>('');
  pageIsConversationSearching = signal<boolean>(false);
  pageConversationSkip = signal<number>(0);
  pageConversationTotalCount = signal<number>(0);
  pageConversationHasMore = signal<boolean>(true);

  /**
   * Resets all store states to their initial values, preparing for a new chat conversation.
   * This includes clearing the conversation ID, message history, prompt button state,
   * streaming flags and current stream content.
   *
   * @remarks
   * This method performs a complete reset of the store by:
   * - Clearing the conversation identifier
   * - Emptying the messages array
   * - Enabling the prompt button
   * - Resetting streaming states
   * - Clearing stream content
   * - Initializing a new empty stream message
   */
  clearForNewConversation(): void {
    this.conversation.set(null);
    this.messages.set([]);
    this.isStreaming.set(false);
    this.showStreamLoader.set(false);
    this.stream.set('');
    this.streamMessage.set(createMessage('', false, undefined));
  }

  /**
   * Sets the search filter for menu conversations
   *
   * @param query - The search query string to filter conversations by
   */
  setMenuConversationSearchFilter(query: string): void {
    this.menuConversationSearchFilter.set(query);
  }

  /**
   * Clears the menu conversation search filter, resetting it to an empty string
   */
  clearMenuConversationSearchFilter(): void {
    this.menuConversationSearchFilter.set('');
  }

  /**
   * Sets the searching state for menu conversations
   *
   * @param isSearching - Boolean flag indicating whether a search operation is in progress
   */
  setMenuConversationSearching(isSearching: boolean): void {
    this.menuIsConversationSearching.set(isSearching);
  }

  /**
   * Updates the menu conversations with the provided array of conversation data transfer objects
   *
   * @param conversations - An array of ConversationDto objects to set as the current menu conversations
   */
  updateMenuConversations(conversations: ConversationDto[]): void {
    this.menuConversations.set(conversations);
  }

  /**
   * Sets the search filter for page conversations
   *
   * @param query - The search query string to filter conversations by
   */
  setPageConversationSearchFilter(query: string): void {
    this.pageConversationSearchFilter.set(query);
  }

  /**
   * Clears the page conversation search filter, resetting it to an empty string
   */
  clearPageConversationSearchFilter(): void {
    this.pageConversationSearchFilter.set('');
  }

  /**
   * Sets the searching state for page conversations
   *
   * @param isSearching - Boolean flag indicating whether a search operation is in progress
   */
  setPageConversationSearching(isSearching: boolean): void {
    this.pageIsConversationSearching.set(isSearching);
  }

  /**
   * Sets the skip offset for page conversation pagination
   *
   * @param skip - The number of conversations to skip when fetching the next page
   */
  setPageConversationSkip(skip: number): void {
    this.pageConversationSkip.set(skip);
  }

  /**
   * Sets the total count of page conversations available
   *
   * @param totalCount - The total number of conversations available for pagination
   */
  setPageConversationTotalCount(totalCount: number): void {
    this.pageConversationTotalCount.set(totalCount);
  }

  /**
   * Sets whether there are more page conversations available to load
   *
   * @param hasMore - Boolean flag indicating if additional conversations can be loaded
   */
  setPageConversationHasMore(hasMore: boolean): void {
    this.pageConversationHasMore.set(hasMore);
  }

  /**
   * Updates the page conversations with the provided array of conversation data transfer objects
   *
   * @param conversations - An array of ConversationDto objects to set as the current page conversations
   */
  updatePageConversations(conversations: ConversationDto[]): void {
    this.pageConversations.set(conversations);
  }
}
