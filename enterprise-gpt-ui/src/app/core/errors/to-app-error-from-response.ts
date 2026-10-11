import { AppError } from './app-error';
import { buildAppError } from './build-app-error';
import { parseProblemDetails } from './problem-details';

/** Structural, not `Response`: Node and jsdom put the class in different realms under test. */
export interface ResponseLike {
  readonly status: number;
  readonly url?: string;
  readonly bodyUsed?: boolean;
  readonly headers?: { get(name: string): string | null };
  text(): Promise<string>;
}

/**
 * Reads a failed `fetch` response's problem body. Never rejects or hangs: an unreadable or
 * stalled body degrades to the plain `http` arm.
 *
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

const BODY_READ_TIMEOUT_MS = 5_000;

async function readProblem(response: ResponseLike) {
  // A body already consumed elsewhere would throw on a second read.
  if (response.bodyUsed) {
    return null;
  }

  const contentType = response.headers?.get('content-type') ?? '';
  if (contentType !== '' && !contentType.includes('json')) {
    return null;
  }

  let text: string | null;
  try {
    // Raced against a timeout: a body that stalls after its headers would hang the turn.
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
    // The losing read keeps running; a later rejection would surface as a fresh unhandled error.
    return await Promise.race([read.catch(() => null), expiry]);
  } finally {
    clearTimeout(timer);
  }
}
