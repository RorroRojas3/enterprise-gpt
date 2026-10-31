import { InjectionToken } from '@angular/core';

/** What a session's `TurnStore` knows about its place on screen. */
export interface ChatSessionContext {
  /** Whether `Chat` shows this session; a background session must not navigate. */
  readonly isActive: () => boolean;
}

/** One per session, provided by `ChatSessions`. */
export const CHAT_SESSION = new InjectionToken<ChatSessionContext>('CHAT_SESSION');
