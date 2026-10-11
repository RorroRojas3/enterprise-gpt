import { EXPORT_FORMAT_LABELS } from '@domain/api/conversation-export';
import { formatBytes } from '@domain/format/bytes';
import { AppError, AppErrorKind } from './app-error';

/**
 * Which arms toast. A cancellation is the user's own Stop, and validation errors already render on
 * their fields. A total table, so a new arm must decide.
 */
const NOTIFIABLE_KINDS = {
  'validation-error': false,
  'upload-too-large': true,
  'resource-not-found': true,
  forbidden: true,
  'permission-required': true,
  'conversation-busy': true,
  'mcp-server-unavailable': true,
  // The dialog the client opens is the message; a toast beside it says it twice.
  'mcp-credential-required': false,
  'mcp-credential-rejected': false,
  'provider-not-configured': true,
  'storage-not-configured': true,
  'export-renderer-not-configured': true,
  http: true,
  network: true,
  aborted: false,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

export function shouldNotify(error: AppError): boolean {
  return NOTIFIABLE_KINDS[error.kind];
}

/**
 * Which arms a person pressing Retry could get past, unlike `isTransientRetriable`'s automatic
 * GET replay. A Retry that cannot work reads as a broken button.
 */
const RETRYABLE_KINDS = {
  'validation-error': false,
  'upload-too-large': false,
  'resource-not-found': false,
  forbidden: false,
  'permission-required': false,
  // The turn in front of it finishes and the next attempt works.
  'conversation-busy': true,
  'mcp-server-unavailable': true,
  // Retry is the wrong control: the way past these is supplying a key, not asking again.
  'mcp-credential-required': false,
  'mcp-credential-rejected': false,
  'provider-not-configured': false,
  'storage-not-configured': false,
  // A deployment either has the renderer or does not; a second press cannot install one.
  'export-renderer-not-configured': false,
  http: true,
  network: true,
  aborted: true,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

export function canRetry(error: AppError): boolean {
  // The `http` arm spans a routing 404 and 5xx, so the status decides within it.
  return error.kind === 'http'
    ? error.status >= 500 || error.status === 408
    : RETRYABLE_KINDS[error.kind];
}

/** Toast or panel headline: an actionable sentence, not the developer-facing `detail`. */
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
    case 'mcp-server-unavailable':
      return error.serverName
        ? `The tool server "${error.serverName}" could not be reached.`
        : 'A tool server could not be reached.';
    case 'mcp-credential-required':
      return error.serverName
        ? `Connect your account to use the tool server "${error.serverName}".`
        : 'Connect your account to use that tool server.';
    case 'mcp-credential-rejected':
      return error.serverName
        ? `The tool server "${error.serverName}" rejected your API key. Enter a new one.`
        : 'That tool server rejected your API key. Enter a new one.';
    case 'provider-not-configured':
      return 'The selected model is not available in this environment.';
    case 'storage-not-configured':
      return 'File storage is not available in this environment.';
    // Names the format: the download menu offers several and only this one is unavailable.
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
 * The toast's second line. The trace id is body-only and a 499 has no body, so its absence gets
 * a real sentence.
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
