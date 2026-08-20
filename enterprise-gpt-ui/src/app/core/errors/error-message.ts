import { EXPORT_FORMAT_LABELS } from '@domain/api/conversation-export';
import { formatBytes } from '@domain/format/bytes';
import { AppError, AppErrorKind } from './app-error';

/**
 * Which arms deserve a toast.
 *
 * A cancellation is the user pressing Stop, so announcing it as an error tells
 * them their own action failed. Validation errors render against the fields that
 * caused them, so a toast would duplicate a message already on screen.
 *
 * `satisfies` rather than an annotation, so a new arm cannot be added without
 * deciding whether it is worth interrupting the user for.
 */
const NOTIFIABLE_KINDS = {
  'validation-error': false,
  'upload-too-large': true,
  'resource-not-found': true,
  forbidden: true,
  'permission-required': true,
  'conversation-busy': true,
  'mcp-authorization-required': true,
  'mcp-server-unavailable': true,
  'provider-not-configured': true,
  'storage-not-configured': true,
  'export-renderer-not-configured': true,
  http: true,
  network: true,
  aborted: false,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

/**
 * Whether an error is worth raising a toast for.
 *
 * @param error The normalized error.
 */
export function shouldNotify(error: AppError): boolean {
  return NOTIFIABLE_KINDS[error.kind];
}

/**
 * Which arms a *person* pressing Retry could plausibly get past.
 *
 * Distinct from `retry-policy.ts`'s `isTransientRetriable`, which decides whether the
 * interceptor may replay a GET automatically: this is the broader, slower question of
 * whether to offer the control at all. A 500 or a dropped connection is worth another
 * go; a 403, a 404 or a missing grant will answer identically however many times it is
 * asked, and a Retry that cannot work reads as a broken button.
 *
 * `satisfies` rather than an annotation, for the same reason as above: a new arm cannot
 * be added without deciding.
 */
const RETRYABLE_KINDS = {
  'validation-error': false,
  'upload-too-large': false,
  'resource-not-found': false,
  forbidden: false,
  'permission-required': false,
  // The whole point of the 409: the turn in front of it finishes and the next one works.
  'conversation-busy': true,
  // Consent is interactive and happens elsewhere; retrying the same call cannot supply it.
  'mcp-authorization-required': false,
  'mcp-server-unavailable': true,
  'provider-not-configured': false,
  'storage-not-configured': false,
  // A deployment either has the renderer or does not; a second press cannot install one.
  'export-renderer-not-configured': false,
  http: true,
  network: true,
  aborted: true,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

/**
 * Whether to offer the user a Retry for this failure.
 *
 * @param error The normalized error.
 */
export function canRetry(error: AppError): boolean {
  // A 404 on a route that does not exist is not going to start existing, but the `http`
  // arm also covers 5xx, so the status decides within it.
  return error.kind === 'http'
    ? error.status >= 500 || error.status === 408
    : RETRYABLE_KINDS[error.kind];
}

/**
 * The headline for an error toast or inline error panel.
 *
 * Prefers a specific, actionable sentence over the server's `detail`, which is
 * written for a developer reading a log.
 *
 * @param error The normalized error.
 */
export function userMessage(error: AppError): string {
  switch (error.kind) {
    case 'validation-error':
      return 'Please correct the highlighted fields and try again.';
    case 'upload-too-large':
      return error.maxBytes > 0
        ? `That file is too large. The maximum upload size is ${formatBytes(error.maxBytes)}.`
        : 'That file is too large to upload.';
    case 'resource-not-found':
      return 'That item no longer exists, or you do not have access to it.';
    case 'forbidden':
      return 'You do not have access to that.';
    case 'permission-required':
      return error.permissions.length > 0
        ? `This action requires the ${formatList(error.permissions)} permission${error.permissions.length > 1 ? 's' : ''}.`
        : 'You do not have permission to do that.';
    case 'conversation-busy':
      return 'This conversation already has a response in progress. Try again once it finishes.';
    // "your authorization" would be false: consent is an administrative act
    // here, and there is no flow to send the user to (US-412). This string is
    // the only channel a screen-reader user gets — the notice card is not a
    // live region — so it has to agree with what the card says.
    case 'mcp-authorization-required':
      return error.serverName
        ? `The tool server "${error.serverName}" requires authorization. Contact your administrator.`
        : 'That tool server requires authorization. Contact your administrator.';
    case 'mcp-server-unavailable':
      return error.serverName
        ? `The tool server "${error.serverName}" could not be reached.`
        : 'A tool server could not be reached.';
    case 'provider-not-configured':
      return 'The selected model is not available in this environment.';
    case 'storage-not-configured':
      return 'File storage is not available in this environment.';
    // Names the format, because the download menu offers three and only one of them
    // is unavailable — "downloads are unavailable" would be wrong about the other two.
    case 'export-renderer-not-configured':
      return error.format
        ? `${EXPORT_FORMAT_LABELS[error.format] ?? error.format.toUpperCase()} download isn’t available in this environment.`
        : 'That download format isn’t available in this environment.';
    case 'http':
      return httpMessage(error.status);
    case 'network':
    case 'aborted':
    case 'client':
      return error.message;
  }
}

/**
 * The secondary line of an error toast: the correlation id support will ask for.
 *
 * `traceId` is body-only — the API exposes no trace header — and a 499 carries no
 * body at all, so the absent case needs a real sentence rather than a blank.
 *
 * @param error The normalized error.
 */
export function traceLine(error: AppError): string {
  return error.traceId ? `Trace ID: ${error.traceId}` : 'No trace ID was returned.';
}

function httpMessage(status: number): string {
  switch (status) {
    case 401:
      return 'Your session has expired. Sign in again to continue.';
    case 404:
      return 'That address does not exist.';
    case 413:
      return 'That file is too large to upload.';
    default:
      return status >= 500
        ? 'The server could not complete the request. Please try again.'
        : 'The request could not be completed.';
  }
}

function formatList(items: readonly string[]): string {
  if (items.length <= 1) {
    return items[0] ?? '';
  }

  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}
