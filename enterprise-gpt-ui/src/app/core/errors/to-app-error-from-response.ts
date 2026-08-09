import { AppError } from './app-error';
import { buildAppError } from './build-app-error';
import { parseProblemDetails } from './problem-details';

/**
 * The minimum of `Response` this normalizer needs.
 *
 * Structurally typed rather than `Response` itself: Node and jsdom put the class
 * in different realms, so `instanceof` is unreliable under test, and a plain
 * object literal is enough to exercise every branch.
 */
export interface ResponseLike {
  readonly status: number;
  readonly url?: string;
  readonly bodyUsed?: boolean;
  readonly headers?: { get(name: string): string | null };
  text(): Promise<string>;
}

/**
 * Normalizes a failed `fetch` response into one {@link AppError}, reading the
 * problem body if there is one.
 *
 * The streaming transport calls this the moment `response.ok` is false, before it
 * reads a single byte of the stream — the chat endpoint sets its
 * `text/event-stream` headers lazily on the first frame, so every pre-stream
 * failure arrives as ordinary `application/problem+json`.
 *
 * Never rejects, and never hangs: a body that cannot be read, or that stalls,
 * degrades to the plain `http` arm.
 *
 * @param response The failed response.
 * @param url Overrides `response.url` when the caller knows the request URL.
 */
export async function toAppErrorFromResponse(
  response: ResponseLike,
  url?: string,
): Promise<AppError> {
  const requestUrl = url ?? (response.url !== '' ? response.url : undefined) ?? null;

  return buildAppError({
    status: response.status,
    problem: await readProblem(response),
    url: requestUrl,
    cause: response,
  });
}

/** How long to wait for an error body before giving up on it. */
const BODY_READ_TIMEOUT_MS = 5_000;

async function readProblem(response: ResponseLike) {
  // A 499 carries no body at all, and a stream already consumed elsewhere would
  // throw on a second read.
  if (response.bodyUsed) {
    return null;
  }

  const contentType = response.headers?.get('content-type') ?? '';
  if (contentType !== '' && !contentType.includes('json')) {
    return null;
  }

  let text: string | null;
  try {
    // Read exactly once: the body is not replayable. Raced against a timeout
    // because a response whose headers arrived but whose body stalls would
    // otherwise leave the caller's promise pending forever — the turn would hang
    // with neither a value nor an error.
    text = await withTimeout(response.text());
  } catch {
    return null;
  }

  if (text === null || text.trim() === '') {
    return null;
  }

  try {
    return parseProblemDetails(JSON.parse(text));
  } catch {
    return null;
  }
}

async function withTimeout(read: Promise<string>): Promise<string | null> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const expiry = new Promise<null>((resolve) => {
    timer = setTimeout(() => resolve(null), BODY_READ_TIMEOUT_MS);
  });

  try {
    // The losing promise keeps running. Swallowing its rejection here matters:
    // otherwise a stalled body that fails later surfaces as an unhandled
    // rejection, which `provideBrowserGlobalErrorListeners()` reports as a fresh
    // error seconds after this one was already handled.
    return await Promise.race([read.catch(() => null), expiry]);
  } finally {
    clearTimeout(timer);
  }
}
