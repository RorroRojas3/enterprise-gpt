import { AnyProblemDetails } from './problem-details';

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
  /** The URL actually requested, query string included, which `instance` omits. */
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

/** A 502: an MCP server could not be reached. */
export interface McpServerUnavailableAppError extends AppErrorBase {
  readonly kind: 'mcp-server-unavailable';
  readonly serverName: string;
}

/**
 * A 428: a selected tool server needs a user-supplied API key and none is usable. The id lets
 * the client open that server's key dialog.
 */
export interface McpCredentialRequiredAppError extends AppErrorBase {
  readonly kind: 'mcp-credential-required';
  readonly mcpServerId: string;
  readonly serverName: string;
}

/** A 428: a selected tool server refused the API key the user stored. */
export interface McpCredentialRejectedAppError extends AppErrorBase {
  readonly kind: 'mcp-credential-rejected';
  readonly mcpServerId: string;
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

/** A 503 no retry can fix: this deployment has no renderer for the requested export format. */
export interface ExportRendererNotConfiguredAppError extends AppErrorBase {
  readonly kind: 'export-renderer-not-configured';
  /** The `?format=` token the request used. Empty when the server sent none. */
  readonly format: string;
}

/**
 * Any HTTP failure without an application problem type: 401, a routing 404 ("no such route"),
 * Kestrel's bare 413 for a chunked upload that bypasses the size filter, 499, and untyped 5xx.
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
 * Every failure the client can observe. A stream that stops before `Finished` is a 200 and a
 * "cut off" turn, not an error; Kestrel's bare 413 is an {@link HttpAppError} with no `maxBytes`.
 */
export type AppError =
  | ValidationAppError
  | UploadTooLargeAppError
  | ResourceNotFoundAppError
  | ForbiddenAppError
  | PermissionRequiredAppError
  | ConversationBusyAppError
  | McpServerUnavailableAppError
  | McpCredentialRequiredAppError
  | McpCredentialRejectedAppError
  | ProviderNotConfiguredAppError
  | StorageNotConfiguredAppError
  | ExportRendererNotConfiguredAppError
  | HttpAppError
  | NetworkAppError
  | AbortedAppError
  | ClientAppError;

export type AppErrorKind = AppError['kind'];

/**
 * A total table, so a new {@link AppError} arm left unclassified fails to compile instead of
 * defaulting; retry policy depends on the classification.
 */
const PROBLEM_KINDS = {
  'validation-error': true,
  'upload-too-large': true,
  'resource-not-found': true,
  forbidden: true,
  'permission-required': true,
  'conversation-busy': true,
  'mcp-server-unavailable': true,
  'mcp-credential-required': true,
  'mcp-credential-rejected': true,
  'provider-not-configured': true,
  'storage-not-configured': true,
  'export-renderer-not-configured': true,
  http: false,
  network: false,
  aborted: false,
  client: false,
} as const satisfies Record<AppErrorKind, boolean>;

/** Whether the error came from an application-specific problem body. */
export function isProblemAppError(error: AppError): boolean {
  return PROBLEM_KINDS[error.kind];
}
