import { UrlMatchResult, UrlSegment } from '@angular/router';

/** The route parameter the chat matcher posts, and the name of `Chat`'s input. */
export const CONVERSATION_ID_PARAM = 'conversationId';

const CONVERSATION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Matches both `/chat` and `/chat/{conversationId}` from **one** route configuration.
 *
 * That is the whole point of a matcher here rather than two sibling routes. The
 * router's default `shouldReuseRoute` compares `future.routeConfig === curr.routeConfig`,
 * so one config means moving between the two URLs changes a parameter instead of
 * destroying and recreating the component — which is what US-401 needs when it creates
 * a conversation around the first prompt and updates the URL with `Location.replaceState`
 * while the answer is already streaming into the composer's page.
 *
 * A second segment that is not a conversation id returns `null` rather than matching
 * with a nonsense parameter, so `/chat/garbage` falls through to the wildcard route
 * instead of issuing `GET api/conversations/garbage`.
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
