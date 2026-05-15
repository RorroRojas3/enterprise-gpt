import { HttpClient } from '@angular/common/http';
import { inject, Injectable, NgZone } from '@angular/core';
import { catchError, EMPTY, finalize, map, Observable, tap } from 'rxjs';
import { ChatConversationDto, ConversationDto } from '../dtos/ConversationDto';
import { environment } from '../../environments/environment';
import { StoreService } from '../store/store.service';
import { ModelStore } from '../store/model.store';
import { McpStore } from '../store/mcp.store';
import { MsalService } from '@azure/msal-angular';
import { CreateChatStreamActionDto } from '../dtos/actions/chats/CreateChatStreamActionDto';
import { PaginatedResponseDto } from '../dtos/PaginatedResponseDto';
import { DeactivateConversationBulkActionDto } from '../dtos/actions/conversation/DeactivateConversationBulkActionDto';
import { UpdateConversationActionDto } from '../dtos/actions/conversation/UpdateConversationActionDto';
import { CreateConversationActionDto } from '../dtos/actions/conversation/CreateConversationActionDto';
import { NotificationService } from './notification.service';

@Injectable({
  providedIn: 'root',
})
export class ConversationService {
  private readonly http = inject(HttpClient);
  private readonly storeService = inject(StoreService);
  private readonly modelStore = inject(ModelStore);
  private readonly mcpStore = inject(McpStore);
  private readonly zone = inject(NgZone);
  private readonly msalService = inject(MsalService);
  private readonly notificationService = inject(NotificationService);

  /**
   * Creates an Observable that streams server-sent events from the chat API.
   *
   * This method establishes a streaming connection to the chat service endpoint,
   * sending a chat prompt and receiving real-time response data as it becomes available.
   * The stream is processed using the Fetch API with ReadableStream and decoded as text.
   *
   * @param prompt - The chat message or prompt to send to the AI service
   * @returns An Observable that emits string chunks of the streaming response
   *
   * @throws Will emit an error if the HTTP request fails or if there are network issues
   * @throws Will emit an error if the response body is null or cannot be read
   * @throws Will emit an error if authentication fails or token cannot be acquired
   */
  getServerSentEvent(prompt: string): Observable<string> {
    return new Observable((observer) => {
      let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
      const decoder = new TextDecoder();
      const abortController = new AbortController();

      const readStream = async (
        streamReader: ReadableStreamDefaultReader<Uint8Array>,
      ) => {
        try {
          while (true) {
            const { value, done } = await streamReader.read();
            if (done) break;

            const text = decoder.decode(value, { stream: true });
            this.zone.run(() => observer.next(text));
          }
        } catch (error) {
          // Don't emit error if it's an abort error
          if (error instanceof Error && error.name === 'AbortError') {
            this.zone.run(() => observer.complete());
          } else {
            this.zone.run(() => observer.error(error));
          }
        } finally {
          this.zone.run(() => observer.complete());
        }
      };

      // Acquire access token from MSAL
      const acquireToken = async () => {
        try {
          const account = this.msalService.instance.getActiveAccount();
          if (!account) {
            throw new Error('No active account! Please sign in.');
          }

          const tokenResponse =
            await this.msalService.instance.acquireTokenSilent({
              scopes: environment.apiConfig.scopes,
              account: account,
            });

          return tokenResponse.accessToken;
        } catch (error) {
          // If silent token acquisition fails, try interactive
          const tokenResponse =
            await this.msalService.instance.acquireTokenPopup({
              scopes: environment.apiConfig.scopes,
            });
          return tokenResponse.accessToken;
        }
      };

      // Start the fetch request with authentication
      acquireToken()
        .then((accessToken) => {
          return fetch(
            `${environment.apiUrl}conversations/${
              this.storeService.conversation()?.id
            }/stream`,
            {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                Accept: 'text/event-stream',
                Authorization: `Bearer ${accessToken}`,
              },
              body: JSON.stringify(
                new CreateChatStreamActionDto(
                  prompt,
                  this.modelStore.selectedModel()!.id,
                  this.modelStore.selectedModel()!.aiServiceId,
                  this.mcpStore.selectedMcps(),
                ),
              ),
              signal: abortController.signal,
            },
          );
        })
        .then((response) => {
          if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
          }
          if (!response.body) {
            throw new Error('Response body is null');
          }
          reader = response.body.getReader();
          return readStream(reader);
        })
        .catch((error) => {
          // Don't emit error if it's an abort error
          if (error instanceof Error && error.name === 'AbortError') {
            this.zone.run(() => observer.complete());
          } else {
            this.zone.run(() => observer.error(error));
          }
        });

      // Teardown logic: cancel the stream and abort the fetch request
      return () => {
        abortController.abort();
        reader?.cancel();
      };
    });
  }

  /**
   * Retrieves the messages for the current conversation.
   *
   * @returns An Observable that emits a ChatConversationDto containing the conversation messages
   * @throws HttpErrorResponse if the request fails or conversation is invalid
   */
  getConversationMessages(id: string): Observable<ChatConversationDto> {
    return this.http.get<ChatConversationDto>(
      `${environment.apiUrl}conversations/${id}/messages`,
    );
  }

  /**
   * Retrieves a specific conversation by its unique identifier.
   *
   * @param {string} conversationId - The unique identifier of the conversation to retrieve
   * @returns {Observable<ConversationDto>} An Observable that emits the ConversationDto for the requested conversation
   */
  getConversation(conversationId: string): Observable<ConversationDto> {
    return this.http.get<ConversationDto>(
      `${environment.apiUrl}conversations/${conversationId}`,
    );
  }

  /**
   * Creates a new conversation by sending a POST request to the conversations endpoint.
   *
   * @param request - The data transfer object containing the information needed to create a conversation
   * @returns An Observable that emits the created ConversationDto upon successful completion
   */
  createConversation(request: CreateConversationActionDto): Observable<ConversationDto> {
    return this.http.post<ConversationDto>(
      `${environment.apiUrl}conversations`,
      request,
    );
  }

  /**
   * Searches for conversations based on a filter string with pagination support.
   *
   * @param filter - The search filter string to match against conversations
   * @param skip - The number of records to skip for pagination (default: 0)
   * @param take - The number of records to retrieve (default: 10)
   * @returns An Observable that emits a paginated response of ConversationDto objects matching the search criteria
   */
  searchConversations(
    filter: string,
    skip: number = 0,
    take: number = 10,
  ): Observable<PaginatedResponseDto<ConversationDto>> {
    return this.http.get<PaginatedResponseDto<ConversationDto>>(
      `${environment.apiUrl}conversations/search`,
      {
        params: { filter, skip, take },
      },
    );
  }

  /**
   * Deactivates a conversation by sending a DELETE request to the API.
   *
   * @param conversationId - The unique identifier of the conversation to deactivate
   * @returns An Observable that completes when the conversation is successfully deactivated
   */
  deactivateConversation(conversationId: string): Observable<void> {
    return this.http.delete<void>(
      `${environment.apiUrl}conversations/${conversationId}`,
    );
  }

  /**
   * Deactivates multiple conversations in bulk.
   *
   * @param request - The bulk deactivation request containing conversation identifiers to deactivate
   * @returns An Observable that completes when the conversations have been deactivated
   */
  deactivateConversationBulk(
    request: DeactivateConversationBulkActionDto,
  ): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}conversations/bulk`, {
      body: request,
    });
  }

  /**
   * Updates an existing conversation by sending a PUT request to the conversations endpoint.
   *
   * @param request - The update conversation request containing the conversation identifier and updated properties
   * @returns An Observable that emits the updated ConversationDto upon successful completion
   */
  updateConversation(request: UpdateConversationActionDto): Observable<ConversationDto> {
    return this.http.put<ConversationDto>(
      `${environment.apiUrl}conversations`,
      request,
    );
  }

  /**
   * Loads and updates the menu conversations in the store.
   *
   * Fetches conversations based on the current menu search filter and updates the store
   * with the results. Shows an error notification if the request fails.
   * Sets the menu conversation searching state while the request is in progress.
   */
  loadMenuConversations(): Observable<void> {
    this.storeService.setMenuConversationSearching(true);
    return this.searchConversations(
      this.storeService.menuConversationSearchFilter(),
      0,
      this.storeService.CONVERSATION_PAGE_SIZE,
    ).pipe(
      tap((response) => {
        this.storeService.updateMenuConversations(response.items);
      }),
      catchError(() => {
        this.notificationService.error('Error loading chats.');
        return EMPTY;
      }),
      finalize(() => this.storeService.setMenuConversationSearching(false)),
      map(() => void 0),
    );
  }

  /**
   * Loads page conversations with the specified filter and pagination parameters.
   *
   * @param filter - The search filter string to match against conversations
   * @param skip - The number of records to skip for pagination (default: 0)
   * @param take - The number of records to retrieve (default: 10)
   */
  loadPageConversations(
    filter: string,
    skip: number = 0,
    take: number = 10,
  ): Observable<void> {
    this.storeService.setPageConversationSearching(true);
    this.storeService.setPageConversationSearchFilter(filter);
    this.storeService.setPageConversationSkip(skip);
    return this.searchConversations(filter, skip, take).pipe(
      tap((response) => {
        this.handlePageConversationsResponse(
          response.items,
          response.totalCount,
        );
      }),
      catchError(() => {
        this.notificationService.error('Error loading chats.');
        return EMPTY;
      }),
      finalize(() => this.storeService.setPageConversationSearching(false)),
      map(() => void 0),
    );
  }

  /**
   * Clears all page conversations data from the store.
   *
   * Resets the page conversations array to empty, resets the skip offset to 0,
   * and sets the total count to 0.
   */
  clearPageConversations(): void {
    this.storeService.updatePageConversations([]);
    this.storeService.setPageConversationSkip(0);
    this.storeService.setPageConversationTotalCount(0);
  }

  /**
   * Handles the response from the page conversations API and updates the store accordingly.
   *
   * @param items - Array of conversation DTOs returned from the API
   * @param totalCount - Total number of conversations available
   * @param append - If true, appends items to existing conversations; if false, replaces them. Defaults to false
   */
  handlePageConversationsResponse(
    items: ConversationDto[],
    totalCount: number,
    append = false,
  ): void {
    this.storeService.setPageConversationTotalCount(totalCount);
    this.storeService.setPageConversationHasMore(
      this.storeService.pageConversationSkip() +
        this.storeService.CONVERSATION_PAGE_SIZE <
        totalCount,
    );

    if (append) {
      this.storeService.updatePageConversations([
        ...this.storeService.pageConversations(),
        ...items,
      ]);
    } else {
      this.storeService.updatePageConversations(items);
    }
  }
}
