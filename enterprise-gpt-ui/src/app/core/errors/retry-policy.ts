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

export const RETRY_POLICY = new InjectionToken<RetryPolicy>('RETRY_POLICY', {
  providedIn: 'root',
  factory: () => DEFAULT_RETRY_POLICY,
});

const RETRIABLE_STATUSES: readonly number[] = [502, 503, 504];

/**
 * Gateway statuses and transport failures only. A 409 `conversation-busy` would race the user's
 * other tab, and a typed 503 is a deployment state no retry changes. Callers restrict this to
 * idempotent requests.
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
 * Full-jitter backoff, a uniform draw from `[0, min(cap, base * 2^attempt))`, so clients never
 * retry in lockstep against a recovering server.
 *
 * @param attempt Zero-based retry index.
 */
export function computeBackoffDelay(attempt: number, policy: RetryPolicy): number {
  const window = Math.min(policy.capMs, policy.baseDelayMs * 2 ** Math.max(0, attempt));

  return Math.floor(policy.random() * window);
}

export type AuthErrorDecision = 'refresh' | 'passthrough';

/** Only a bare 401 refreshes the token; a 403 means the grant is wrong, which no refresh fixes. */
export function authErrorDecision(error: AppError): AuthErrorDecision {
  return AUTH_DECISIONS[error.kind] === 'refresh' && error.status === 401
    ? 'refresh'
    : 'passthrough';
}

/** Exported so a spec asserts the key set: a new arm fails the suite as well as the compiler. */
export const AUTH_DECISIONS = {
  'validation-error': 'passthrough',
  'upload-too-large': 'passthrough',
  'resource-not-found': 'passthrough',
  forbidden: 'passthrough',
  'permission-required': 'passthrough',
  'conversation-busy': 'passthrough',
  'mcp-server-unavailable': 'passthrough',
  // 428s ask the user for a credential, which no token refresh supplies.
  'mcp-credential-required': 'passthrough',
  'mcp-credential-rejected': 'passthrough',
  'provider-not-configured': 'passthrough',
  'storage-not-configured': 'passthrough',
  'export-renderer-not-configured': 'passthrough',
  // The only arm a 401 can land on, since a 401 carries no application type.
  http: 'refresh',
  network: 'passthrough',
  aborted: 'passthrough',
  client: 'passthrough',
} as const satisfies Record<AppErrorKind, AuthErrorDecision>;
