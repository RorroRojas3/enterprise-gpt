import { Injectable, inject } from '@angular/core';
import { Observable, Subscriber, bufferTime, filter } from 'rxjs';
import { ApiUrl } from '@core/api/api-url';
import { AssistantUiEvent } from '@core/api/assistant-ui-contract';
import { AccessTokenClient } from '@core/auth/access-token-client';
import { APP_CONFIG } from '@core/config/app-config-token';
import { AppError } from '@core/errors/app-error';
import { authErrorDecision } from '@core/errors/retry-policy';
import { toAppError } from '@core/errors/to-app-error';
import { toAppErrorFromResponse } from '@core/errors/to-app-error-from-response';
import { injectSignedOutAbort } from '@core/events/session-events';
import { STREAM_FETCH } from './stream-fetch-token';
import { createRawTextCodec } from './stream/raw-text-codec';
import { createSseFrameCodec } from './stream/sse-frame-codec';
import { StreamCodec } from './stream/stream-codec';

/** One streamed turn against `POST api/conversations/{id}/stream`. */
export interface StreamTurnRequest {
  readonly conversationId: string;
  readonly prompt: string;
  readonly modelId: string;
  /** Sent as `mcpServers: [{ id }]`, always present: `[]` when empty, never omitted. */
  readonly mcpServerIds: readonly string[];
  /** The caller's Stop. Composed with the signed-out signal. */
  readonly signal?: AbortSignal;
}

/**
 * Batch window for decoded events: `bufferTime` is deterministic under test, unlike
 * `requestAnimationFrame`, and ~16ms keeps consumers to one `patchState` per frame.
 */
export const STREAM_BATCH_WINDOW_MS = 16;

/**
 * Raw `fetch`, as `HttpClient` cannot expose a body stream; the token goes only to {@link ApiUrl}
 * URLs. Pre-stream failures error with an {@link AppError}, and a bare 401 replays once (auth is
 * rejected before the conversation lock). Aborting errors as `aborted` after flushed batches; a
 * failure after the body starts completes instead, as the contract has no mid-stream errors.
 */
@Injectable({ providedIn: 'root' })
export class ConversationStreamClient {
  private readonly _apiUrl = inject(ApiUrl);
  private readonly _tokens = inject(AccessTokenClient);
  private readonly _config = inject(APP_CONFIG);
  private readonly _fetch = inject(STREAM_FETCH);
  private readonly _signedOut = injectSignedOutAbort();

  /** Empty batches are never emitted; the residual buffer flushes on completion. */
  stream(request: StreamTurnRequest): Observable<readonly AssistantUiEvent[]> {
    return new Observable<AssistantUiEvent>((subscriber) => {
      const teardown = new AbortController();
      const signal = AbortSignal.any([
        teardown.signal,
        this._signedOut(),
        ...(request.signal ? [request.signal] : []),
      ]);

      void this.run(request, signal, subscriber);

      // Unsubscribing aborts the fetch, so a `switchMap` consumer cancels the request.
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
        // A clean close and a faulting read must deliver the same text, so salvage the
        // codec's buffered tail as on `done`.
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
