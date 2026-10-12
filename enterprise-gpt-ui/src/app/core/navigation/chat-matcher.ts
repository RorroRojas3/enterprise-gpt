import { UrlMatchResult, UrlSegment } from '@angular/router';

/** The route parameter the chat matcher posts, and the name of `Chat`'s input. */
export const CONVERSATION_ID_PARAM = 'conversationId';

const CONVERSATION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * One route config for `/chat` and `/chat/{id}`, so `Chat` is reused, not recreated, when the
 * first prompt writes the new id to the URL mid-stream. A non-id segment falls to the wildcard.
 */
export function chatMatcher(segments: UrlSegment[]): UrlMatchResult | null {
  const [first, second, ...rest] = segments;

  if (first?.path !== 'chat' || rest.length > 0) {
    return null;
  }

  if (second === undefined) {
    return { consumed: [first] };
  }

  return CONVERSATION_ID.test(second.path)
    ? { consumed: [first, second], posParams: { [CONVERSATION_ID_PARAM]: second } }
    : null;
}
