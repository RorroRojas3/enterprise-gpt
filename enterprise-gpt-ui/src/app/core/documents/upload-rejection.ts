import { formatBytes } from '@domain/format/bytes';
import { AppError, AppErrorKind } from '@core/errors/app-error';
import { traceLine, userMessage } from '@core/errors/error-message';

/** A refused upload, ready to be shown as a toast (US-805, frame `4l`). */
export interface UploadRejection {
  readonly title: string;
  readonly detail: string;
  /** Pre-formatted; rendered in the mono face. */
  readonly traceLine: string;
  /** Whether a second attempt could plausibly get past it. */
  readonly retryable: boolean;
}

/**
 * Which upload failures are worth offering a Retry for.
 *
 * A grant is acquired from an administrator, not by asking again, and a file the
 * validator rejected — empty, misnamed, or whose leading bytes contradict its
 * extension — is wrong in a way a second identical POST cannot fix. Frame `4l` draws
 * its permission toast with no Retry for exactly this reason and its size toast with
 * one: the cap is a deployment setting and the failure can also arrive as a bare 413
 * from a request whose declared length was wrong, neither of which is a property of
 * the bytes.
 *
 * `satisfies` rather than an annotation, so a new arm cannot be added to `AppError`
 * without deciding what it means here.
 */
const RETRYABLE_REJECTIONS = {
  'validation-error': false,
  'upload-too-large': true,
  'resource-not-found': false,
  forbidden: false,
  'permission-required': false,
  'conversation-busy': false,
  'mcp-authorization-required': false,
  'mcp-server-unavailable': false,
  'provider-not-configured': false,
  'storage-not-configured': false,
  'export-renderer-not-configured': false,
  http: true,
  network: true,
  aborted: false,
  client: true,
} as const satisfies Record<AppErrorKind, boolean>;

/**
 * Explains why one file was refused, in the terms the user can act on.
 *
 * Deliberately richer than `userMessage` for the size case: the acceptance criterion
 * and the board both quote *both* numbers — "Files can be up to 50 MB — this one is
 * 212 MB." — and only the caller knows the second one.
 *
 * Four rejection shapes reach here, not the two the story names. Beyond
 * `upload-too-large` and `permission-required` there is `validation-error`, which is
 * what an empty file, an over-long name, or leading bytes that contradict the
 * extension produce, and a bare **413** with no `maxBytes` at all, which is what a
 * chunked upload trips when the server lowers its body-size limit mid-request.
 *
 * @param error The normalized failure.
 * @param file The file that was refused.
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
      // The server's own words: `UploadedFileValidator` writes them for users, and
      // they name the actual problem where `userMessage` only says "correct the
      // highlighted fields" — which points at fields this surface does not have.
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
