import { Injectable, inject } from '@angular/core';
import { Observable, Subscriber, bufferTime, filter } from 'rxjs';
import { AssistantUiEvent } from '@domain/stream/andes/assistant-ui.contract';
import { createRawTextCodec } from '@domain/stream/raw-text-codec';
import { createSseFrameCodec } from '@domain/stream/sse-frame-codec';
import { StreamCodec } from '@domain/stream/stream-codec';
import { APP_CONFIG } from '../config/app-config.token';
import { AppError } from '../errors/app-error';
import { authErrorDecision } from '../errors/retry-policy';
import { toAppError } from '../errors/to-app-error';
import { toAppErrorFromResponse } from '../errors/to-app-error-from-response';
import { injectSignedOutAbort } from '../events/session-events';
import { ApiUrl } from '../http/api-url';
import { TokenService } from '../auth/token-service';
import { STREAM_FETCH } from './stream-fetch.token';

/** One streamed turn against `POST api/conversations/{id}/stream`. */
export interface StreamTurnRequest {
  readonly conversationId: string;
  readonly prompt: string;
  readonly modelId: string;
  /**
   * Sent as `mcpServers: [{ id }]` — always present, `[]` when empty. The old
   * client dropped the field entirely, which is the regression US-403 names.
   */
  readonly mcpServerIds: readonly string[];
  /** The caller's Stop (US-407). Composed with the signed-out signal. */
  readonly signal?: AbortSignal;
}

/**
 * How long decoded events are buffered before being delivered as one batch.
 * `bufferTime` rather than `requestAnimationFrame`, because it is deterministic
 * under test without a browser scheduler; ~16ms keeps delivery within one
 * frame at any token rate, so a consumer folding each batch performs at most
 * one `patchState` per flush.
 */
export const STREAM_BATCH_WINDOW_MS = 16;

/**
 * The chat turn transport: an abortable raw `fetch` that reads errors first.
 *
 * Raw `fetch` because `HttpClient` cannot expose a readable body stream; the
 * bearer token therefore comes from the same {@link TokenService} the
 * `authInterceptor` uses, and is only ever attached to a URL built by
 * {@link ApiUrl} — ours by construction.
 *
 * Contract with consumers:
 * - Every value the observable errors with is an {@link AppError}.
 * - A pre-stream failure (bad token, transport failure, non-2xx response)
 *   errors. The server sets its `text/event-stream` headers lazily on the
 *   first frame, so every pre-stream failure carries ordinary
 *   `application/problem+json`, parsed *before* a single body byte is read.
 * - A bare 401 is replayed exactly once with a force-refreshed token, gated by
 *   {@link authErrorDecision} — the same single source of truth the
 *   interceptor consults. The replay is safe because authentication is
 *   rejected before the conversation lock is taken, so it cannot double-start
 *   a turn.
 * - Aborting (the caller's Stop, sign-out, or unsubscribing) errors with the
 *   `aborted` arm — the race-free discriminator against a natural end; every
 *   previously flushed batch has already been delivered, so partial output
 *   survives a Stop (US-407). There is no stop endpoint and none is called.
 * - A failure *after* the body has begun completes gracefully instead: the
 *   contract has no error surface after the first frame, so a mid-stream
 *   fault is indistinguishable from a network drop, and both take the single
 *   path US-406 already owns — a body that ended without `Finished`.
 */
@Injectable({ providedIn: 'root' })
export class ConversationStreamClient {
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _tokens = inject(TokenService);
  private readonly _config = inject(APP_CONFIG);
  private readonly _fetch = inject(STREAM_FETCH);
  private readonly _signedOut = injectSignedOutAbort();

  /**
   * Streams one turn, delivering decoded events in batches of at most one
   * flush per {@link STREAM_BATCH_WINDOW_MS}. Empty batches are never emitted;
   * the residual buffer is flushed on completion.
   */
  stream(request: StreamTurnRequest): Observable<readonly AssistantUiEvent[]> {
    return new Observable<AssistantUiEvent>((subscriber) => {
      const teardown = new AbortController();
      const signal = AbortSignal.any([
        teardown.signal,
        this._signedOut(),
        ...(request.signal ? [request.signal] : []),
      ]);

      void this.run(request, signal, subscriber);

      // Unsubscribing aborts the fetch too, so a `switchMap`-style consumer
      // cancels the network request rather than orphaning it.
      return () => teardown.abort();
    }).pipe(
      bufferTime(STREAM_BATCH_WINDOW_MS),
      filter((batch) => batch.length > 0),
    );
  }

  /** Never rejects: every ending is delivered through the subscriber. */
  private async run(
    request: StreamTurnRequest,
    signal: AbortSignal,
    subscriber: Subscriber<AssistantUiEvent>,
  ): Promise<void> {
    let url: string | undefined;
    let bodyStarted = false;
    let codec: StreamCodec | null = null;

    try {
      url = this._apiUrl.build(`conversations/${ApiUrl.segment(request.conversationId)}/stream`);
      let response = await this.issue(url, request, signal, false);

      if (!response.ok) {
        const error = await toAppErrorFromResponse(response, url);
        if (authErrorDecision(error) !== 'refresh') {
          subscriber.error(error);
          return;
        }

        response = await this.issue(url, request, signal, true);
        if (!response.ok) {
          subscriber.error(await toAppErrorFromResponse(response, url));
          return;
        }
      }

      if (response.body === null) {
        subscriber.complete();
        return;
      }

      codec = this._config.features.rawStreamCodec ? createRawTextCodec() : createSseFrameCodec();

      bodyStarted = true;
      const reader = response.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        for (const event of codec.decode(value)) {
          subscriber.next(event);
        }
      }

      for (const event of codec.flush()) {
        subscriber.next(event);
      }
      subscriber.complete();
    } catch (thrown) {
      const error: AppError = toAppError(thrown, { signal, url });
      if (error.kind !== 'aborted' && bodyStarted) {
        // The contract's two "body just stops" endings — a clean close and a
        // faulting read — must deliver the same text, so the codec's buffered
        // tail is salvaged here exactly as it is on `done`.
        if (codec !== null) {
          try {
            for (const event of codec.flush()) {
              subscriber.next(event);
            }
          } catch {
            // A codec that cannot flush loses only its tail; the turn still
            // takes the graceful-completion path.
          }
        }
        subscriber.complete();
        return;
      }
      subscriber.error(error);
    }
  }

  private async issue(
    url: string,
    request: StreamTurnRequest,
    signal: AbortSignal,
    forceRefresh: boolean,
  ): Promise<Response> {
    const token = await this._tokens.getToken(forceRefresh ? { forceRefresh } : undefined);

    return this._fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({
        prompt: request.prompt,
        modelId: request.modelId,
        mcpServers: request.mcpServerIds.map((id) => ({ id })),
      }),
      signal,
    });
  }
}
