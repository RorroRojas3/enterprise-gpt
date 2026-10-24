import { formatBytes } from '@shared/utils/format/bytes';
import { AppError, AppErrorKind } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';

/** A refused upload, ready to be shown as a toast. */
export interface UploadRejection {
  readonly title: string;
  readonly detail: string;
  /** Pre-formatted; rendered in the mono face. */
  readonly traceLine: string;
  /** Whether a second attempt could plausibly get past it. */
  readonly retryable: boolean;
}

/**
 * Retry only failures that aren't a property of the file or the grant: a size cap is
 * deployment config, and a bare 413 can come from a wrong declared length. `satisfies` forces
 * a choice for every new arm.
 */
const RETRYABLE_REJECTIONS = {
  'validation-error': false,
  'upload-too-large': true,
  'resource-not-found': false,
  forbidden: false,
  'permission-required': false,
  'conversation-busy': false,
  'mcp-server-unavailable': false,
  // Unreachable on an upload, and reattaching the file could not supply an API key anyway.
  'mcp-credential-required': false,
  'mcp-credential-rejected': false,
  'provider-not-configured': false,
  'storage-not-configured': false,
  'export-renderer-not-configured': false,
  http: true,
  network: true,
  aborted: false,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

/**
 * Richer than `userMessage` for size, quoting both the cap and the file's size, which only the
 * caller knows. Also covers `validation-error` and Kestrel's bare 413.
 */
export function describeUploadRejection(
  error: AppError,
  file: { readonly name: string; readonly size: number },
): UploadRejection {
  return {
    ...headline(error, file),
    traceLine: traceLine(error),
    retryable: RETRYABLE_REJECTIONS[error.kind],
  };
}

function headline(
  error: AppError,
  file: { readonly name: string; readonly size: number },
): { title: string; detail: string } {
  const refused = `${file.name} couldn’t be uploaded`;

  switch (error.kind) {
    case 'upload-too-large':
      return {
        title: refused,
        detail:
          error.maxBytes > 0
            ? `Files can be up to ${formatBytes(error.maxBytes)} — this one is ${formatBytes(file.size)}.`
            : `This file is ${formatBytes(file.size)}, which is more than this deployment accepts.`,
      };

    case 'permission-required':
      return {
        title: 'You can’t upload files',
        detail:
          error.permissions.length > 0
            ? `Your account is missing the “${error.permissions.join('”, “')}” permission — ask an administrator.`
            : 'Your account is missing the permission to upload files — ask an administrator.',
      };

    case 'validation-error':
      // The server's own words name the problem; `userMessage` points at form fields.
      return { title: refused, detail: firstValidationMessage(error) ?? userMessage(error) };

    case 'http':
      // A bare 413 from Kestrel, with no `maxBytes` to quote.
      return error.status === 413
        ? {
            title: refused,
            detail: `This file is ${formatBytes(file.size)}, which is more than this deployment accepts.`,
          }
        : { title: refused, detail: userMessage(error) };

    default:
      return { title: refused, detail: userMessage(error) };
  }
}

function firstValidationMessage(
  error: Extract<AppError, { kind: 'validation-error' }>,
): string | null {
  for (const messages of Object.values(error.errors)) {
    const message = messages.find((candidate) => candidate.trim() !== '');
    if (message !== undefined) {
      return message;
    }
  }

  return null;
}
