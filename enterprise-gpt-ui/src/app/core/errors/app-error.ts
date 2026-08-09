import { AnyProblemDetails } from './problem-details';

/**
 * Fields carried by every {@link AppError}, whatever produced it.
 */
export interface AppErrorBase {
  /** HTTP status, or 0 for a transport failure, an abort, or a client-side throw. */
  readonly status: number;
  /** The problem `title`, or a synthesized one. */
  readonly title: string;
  readonly detail: string | null;
  /** Correlation id from the problem body. Null whenever there was no body. */
  readonly traceId: string | null;
  /** The request path from the problem body. Carries no query string. */
  readonly instance: string | null;
  /**
   * The URL the client actually requested. Held separately because `instance`
   * omits the query string and so cannot reconstruct the failing request.
   */
  readonly url: string | null;
  /** One user-facing line, suitable for a toast headline. */
  readonly message: string;
  /** The parsed problem body, when the response carried one. Narrow it on `type`. */
  readonly problem: AnyProblemDetails | null;
  /** The original thrown value or response. For logging only. */
  readonly cause: unknown;
}

/** A 400 carrying per-field validation messages. */
export interface ValidationAppError extends AppErrorBase {
  readonly kind: 'validation-error';
  /** Keyed by the server's own property paths, verbatim and PascalCase. */
  readonly errors: Readonly<Record<string, readonly string[]>>;
}

/** A 400 — not a 413 — from the upload size filter. */
export interface UploadTooLargeAppError extends AppErrorBase {
  readonly kind: 'upload-too-large';
  readonly maxBytes: number;
}

/** A 404 for a resource that does not exist, is deactivated, or belongs to someone else. */
export interface ResourceNotFoundAppError extends AppErrorBase {
  readonly kind: 'resource-not-found';
}

/** A 403 for an operation the caller is not allowed to perform. */
export interface ForbiddenAppError extends AppErrorBase {
  readonly kind: 'forbidden';
}

/** A 403 naming the permission grants the caller is missing. */
export interface PermissionRequiredAppError extends AppErrorBase {
  readonly kind: 'permission-required';
  /** Display names such as `Administrator`, not permission ids. */
  readonly permissions: readonly string[];
}

/** A 409: the conversation already has a turn in flight. Never auto-retried. */
export interface ConversationBusyAppError extends AppErrorBase {
  readonly kind: 'conversation-busy';
}

/**
 * A 403 requiring interactive consent for an MCP server.
 *
 * Must never enter a token-refresh path — see {@link authErrorDecision}.
 */
export interface McpAuthorizationRequiredAppError extends AppErrorBase {
  readonly kind: 'mcp-authorization-required';
  readonly serverName: string;
  /** Supplied only once US-411 has shipped. */
  readonly scope: string | null;
}

/** A 502: an MCP server could not be reached. */
export interface McpServerUnavailableAppError extends AppErrorBase {
  readonly kind: 'mcp-server-unavailable';
  readonly serverName: string;
}

/** A 503: the selected model's provider has no chat client in this deployment. */
export interface ProviderNotConfiguredAppError extends AppErrorBase {
  readonly kind: 'provider-not-configured';
  readonly providerId: string;
}

/** A 503: blob storage cannot sign download links in this deployment. */
export interface StorageNotConfiguredAppError extends AppErrorBase {
  readonly kind: 'storage-not-configured';
}

/**
 * Any HTTP failure with no application-specific problem type.
 *
 * Covers 401, a routing 404 (which carries the RFC 9110 link and means "no such
 * route", not "no such resource"), a bare 413 from Kestrel when a chunked upload
 * bypasses the size filter, 499, 500, and any 5xx without a type.
 */
export interface HttpAppError extends AppErrorBase {
  readonly kind: 'http';
}

/** The request never produced a response: DNS, TLS, offline, or a CORS rejection. */
export interface NetworkAppError extends AppErrorBase {
  readonly kind: 'network';
}

/** The caller aborted the request. Not a failure; the user pressed Stop. */
export interface AbortedAppError extends AppErrorBase {
  readonly kind: 'aborted';
}

/** Anything else that was thrown, including non-Error values. */
export interface ClientAppError extends AppErrorBase {
  readonly kind: 'client';
}

/**
 * Every failure the client can observe, as one discriminated union.
 *
 * Two things are deliberately absent:
 *
 * - A truncated chat stream. A `text/event-stream` body that stops without a
 *   `Finished` event arrives as a successful 200, so it is a turn state the
 *   transcript renders as "cut off", not an error. Normalization never sees it.
 * - A `maxBytes` on a bare 413. Kestrel produces that response, not the upload
 *   filter, so it lands on {@link HttpAppError} and upload UI must handle both.
 */
export type AppError =
  | ValidationAppError
  | UploadTooLargeAppError
  | ResourceNotFoundAppError
  | ForbiddenAppError
  | PermissionRequiredAppError
  | ConversationBusyAppError
  | McpAuthorizationRequiredAppError
  | McpServerUnavailableAppError
  | ProviderNotConfiguredAppError
  | StorageNotConfiguredAppError
  | HttpAppError
  | NetworkAppError
  | AbortedAppError
  | ClientAppError;

/** The discriminant of {@link AppError}. */
export type AppErrorKind = AppError['kind'];

/**
 * Which arms come from an application-specific problem body.
 *
 * A positive table rather than a negation, and `satisfies` rather than an
 * annotation, so adding an arm to {@link AppError} without classifying it is a
 * compile error instead of a silent default. Downstream policy depends on this —
 * a misclassified arm changes whether it is retried.
 */
const PROBLEM_KINDS = {
  'validation-error': true,
  'upload-too-large': true,
  'resource-not-found': true,
  forbidden: true,
  'permission-required': true,
  'conversation-busy': true,
  'mcp-authorization-required': true,
  'mcp-server-unavailable': true,
  'provider-not-configured': true,
  'storage-not-configured': true,
  http: false,
  network: false,
  aborted: false,
  client: false,
} as const satisfies Record<AppErrorKind, boolean>;

/** Whether the error came from an application-specific problem body. */
export function isProblemAppError(error: AppError): boolean {
  return PROBLEM_KINDS[error.kind];
}

/**
 * Whether the error is a routing 404 — no such endpoint — as opposed to
 * `resource-not-found`, which means the caller may not see that row.
 *
 * The distinction matters: a routing 404 is a client bug, while
 * `resource-not-found` is the API's deliberate answer for a resource owned by
 * someone else, and must not be reported as "deleted".
 */
export function isRouteNotFound(error: AppError): boolean {
  return error.kind === 'http' && error.status === 404;
}
