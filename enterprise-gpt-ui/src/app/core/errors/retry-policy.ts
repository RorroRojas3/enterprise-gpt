import { InjectionToken } from '@angular/core';
import { AppError, AppErrorKind, isProblemAppError } from './app-error';

/** Knobs for the transient-failure retry the HTTP interceptor applies. */
export interface RetryPolicy {
  /** Attempts after the first. */
  readonly maxRetries: number;
  /** First backoff window, doubled per attempt. */
  readonly baseDelayMs: number;
  /** Ceiling on the backoff window. */
  readonly capMs: number;
  /** Jitter source. Injectable so specs can pin the bounds. */
  readonly random: () => number;
}

/** Three retries over at most 3.5 seconds of added latency. */
export const DEFAULT_RETRY_POLICY: RetryPolicy = {
  maxRetries: 3,
  baseDelayMs: 500,
  capMs: 5_000,
  random: Math.random,
};

/** Overridable retry configuration. */
export const RETRY_POLICY = new InjectionToken<RetryPolicy>('RETRY_POLICY', {
  providedIn: 'root',
  factory: () => DEFAULT_RETRY_POLICY,
});

const RETRIABLE_STATUSES: readonly number[] = [502, 503, 504];

/**
 * Whether a failure is worth retrying.
 *
 * Gateway statuses and outright transport failures are; everything else is not.
 * Two exclusions carry weight:
 *
 * - **409 `conversation-busy`** is never retried at any interval. A turn is
 *   already running; retrying races the user's other tab.
 * - **A 503 carrying an application problem type** — `provider-not-configured`,
 *   `storage-not-configured`, `export-renderer-not-configured` — is a deterministic
 *   deployment state, not transient
 *   load. Retrying buys three round trips of nothing and delays an actionable
 *   message by seconds.
 *
 * The caller is responsible for restricting this to idempotent requests.
 *
 * @param error The normalized error.
 */
export function isTransientRetriable(error: AppError): boolean {
  if (error.kind === 'network') {
    return true;
  }

  if (error.kind === 'aborted' || error.kind === 'client') {
    return false;
  }

  if (isProblemAppError(error)) {
    return false;
  }

  return RETRIABLE_STATUSES.includes(error.status);
}

/**
 * Full-jitter backoff: a uniform draw from `[0, min(cap, base * 2^attempt))`.
 *
 * Jitter is the point — synchronized clients retrying on the same schedule are
 * what turns a recovering server back into an overloaded one.
 *
 * @param attempt Zero-based retry index.
 * @param policy The active policy.
 */
export function computeBackoffDelay(attempt: number, policy: RetryPolicy): number {
  const window = Math.min(policy.capMs, policy.baseDelayMs * 2 ** Math.max(0, attempt));

  return Math.floor(policy.random() * window);
}

/** What the authentication layer should do with a failure. */
export type AuthErrorDecision = 'refresh' | 'passthrough';

/**
 * Whether a failure warrants acquiring a fresh token.
 *
 * Only a bare 401 does. A `mcp-authorization-required` 403 in particular must
 * never enter the refresh path: it asks for interactive consent to a downstream
 * server, which no number of token refreshes can supply, so refreshing on it
 * produces a loop that cannot terminate. `forbidden` and `permission-required`
 * are 403s for the same reason — the token is fine, the grant is not.
 *
 * Shipped as a standalone decision because the authentication interceptor arrives
 * later; it consults this rather than re-deriving the rule.
 *
 * @param error The normalized error.
 */
export function authErrorDecision(error: AppError): AuthErrorDecision {
  return AUTH_DECISIONS[error.kind] === 'refresh' && error.status === 401
    ? 'refresh'
    : 'passthrough';
}

/**
 * Every arm's refresh behaviour, exhaustively.
 *
 * The `satisfies` is the guarantee: adding a member to {@link AppErrorKind}
 * without deciding whether it may trigger a token refresh is a compile error.
 * Exported so a spec can assert the key set too, which is what makes a new arm
 * fail the suite rather than only the compiler.
 */
export const AUTH_DECISIONS = {
  'validation-error': 'passthrough',
  'upload-too-large': 'passthrough',
  'resource-not-found': 'passthrough',
  forbidden: 'passthrough',
  'permission-required': 'passthrough',
  'conversation-busy': 'passthrough',
  'mcp-authorization-required': 'passthrough',
  'mcp-server-unavailable': 'passthrough',
  'provider-not-configured': 'passthrough',
  'storage-not-configured': 'passthrough',
  'export-renderer-not-configured': 'passthrough',
  // The only arm a 401 can land on, since a 401 carries no application type.
  http: 'refresh',
  network: 'passthrough',
  aborted: 'passthrough',
  client: 'passthrough',
} as const satisfies Record<AppErrorKind, AuthErrorDecision>;
