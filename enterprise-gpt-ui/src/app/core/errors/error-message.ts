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
    case 'mcp-authorization-required':
      return error.serverName
        ? `The tool server "${error.serverName}" needs your authorization before it can be used.`
        : 'A tool server needs your authorization before it can be used.';
    case 'mcp-server-unavailable':
      return error.serverName
        ? `The tool server "${error.serverName}" could not be reached.`
        : 'A tool server could not be reached.';
    case 'provider-not-configured':
      return 'The selected model is not available in this environment.';
    case 'storage-not-configured':
      return 'File storage is not available in this environment.';
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

function formatBytes(bytes: number): string {
  const mb = bytes / (1024 * 1024);

  return mb >= 1 ? `${round(mb)} MB` : `${round(bytes / 1024)} KB`;
}

function round(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

function formatList(items: readonly string[]): string {
  if (items.length <= 1) {
    return items[0] ?? '';
  }

  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}
