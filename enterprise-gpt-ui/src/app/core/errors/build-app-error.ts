import { AppError } from './app-error';
import {
  AnyProblemDetails,
  errorsExtension,
  numberExtension,
  stringArrayExtension,
  stringExtension,
} from './problem-details';
import { PROBLEM_TYPE } from './problem-types';

/** Inputs shared by both normalization entry points. */
export interface AppErrorParts {
  readonly status: number;
  readonly problem: AnyProblemDetails | null;
  readonly url: string | null;
  readonly cause: unknown;
  /** Used only when no problem body supplied a title. */
  readonly fallbackTitle?: string;
}

/**
 * Selects the {@link AppError} arm and fills its fields.
 *
 * The single place arm selection happens, so the synchronous and asynchronous
 * entry points cannot drift.
 *
 * @param parts The normalized inputs.
 */
export function buildAppError(parts: AppErrorParts): AppError {
  const { problem, url, cause } = parts;
  const status = problem?.status ?? parts.status;
  const base = {
    status,
    title: problem?.title ?? parts.fallbackTitle ?? defaultTitle(status),
    detail: problem?.detail ?? null,
    traceId: problem?.traceId ?? null,
    instance: problem?.instance ?? null,
    url,
    problem,
    cause,
  };

  if (problem) {
    switch (problem.type) {
      case PROBLEM_TYPE.validationError:
        return {
          ...base,
          kind: 'validation-error',
          message: base.title,
          errors: errorsExtension(problem),
        };
      case PROBLEM_TYPE.uploadTooLarge:
        return {
          ...base,
          kind: 'upload-too-large',
          message: messageOf(base),
          maxBytes: numberExtension(problem, 'maxBytes') ?? 0,
        };
      case PROBLEM_TYPE.resourceNotFound:
        return { ...base, kind: 'resource-not-found', message: messageOf(base) };
      case PROBLEM_TYPE.forbidden:
        return { ...base, kind: 'forbidden', message: messageOf(base) };
      case PROBLEM_TYPE.permissionRequired:
        return {
          ...base,
          kind: 'permission-required',
          message: messageOf(base),
          permissions: stringArrayExtension(problem, 'permissions'),
        };
      case PROBLEM_TYPE.conversationBusy:
        return { ...base, kind: 'conversation-busy', message: messageOf(base) };
      case PROBLEM_TYPE.mcpAuthorizationRequired:
        return {
          ...base,
          kind: 'mcp-authorization-required',
          message: messageOf(base),
          serverName: stringExtension(problem, 'serverName') ?? '',
          scope: stringExtension(problem, 'scope'),
        };
      case PROBLEM_TYPE.mcpServerUnavailable:
        return {
          ...base,
          kind: 'mcp-server-unavailable',
          message: messageOf(base),
          serverName: stringExtension(problem, 'serverName') ?? '',
        };
      case PROBLEM_TYPE.providerNotConfigured:
        return {
          ...base,
          kind: 'provider-not-configured',
          message: messageOf(base),
          providerId: stringExtension(problem, 'providerId') ?? '',
        };
      case PROBLEM_TYPE.storageNotConfigured:
        return { ...base, kind: 'storage-not-configured', message: messageOf(base) };
      case PROBLEM_TYPE.exportRendererNotConfigured:
        return {
          ...base,
          kind: 'export-renderer-not-configured',
          message: messageOf(base),
          format: stringExtension(problem, 'format') ?? '',
        };
      default:
        break;
    }
  }

  return { ...base, kind: 'http', message: messageOf(base) };
}

/** Builds the arm for a request that never produced a response. */
export function networkAppError(url: string | null, cause: unknown): AppError {
  return {
    kind: 'network',
    status: 0,
    title: 'Network error',
    detail: null,
    traceId: null,
    instance: null,
    url,
    message: 'The server could not be reached. Check your connection and try again.',
    problem: null,
    cause,
  };
}

/** Builds the arm for a request the caller aborted. */
export function abortedAppError(url: string | null, cause: unknown): AppError {
  return {
    kind: 'aborted',
    status: 0,
    title: 'Request cancelled',
    detail: null,
    traceId: null,
    instance: null,
    url,
    message: 'The request was cancelled.',
    problem: null,
    cause,
  };
}

/** Builds the arm for a value thrown outside any HTTP exchange. */
export function clientAppError(cause: unknown): AppError {
  return {
    kind: 'client',
    status: 0,
    title: 'Unexpected error',
    detail: cause instanceof Error ? cause.message : null,
    traceId: null,
    instance: null,
    url: null,
    message: 'Something went wrong. Please try again.',
    problem: null,
    cause,
  };
}

function messageOf(base: { title: string; detail: string | null }): string {
  return base.detail?.trim() ? base.detail : base.title;
}

function defaultTitle(status: number): string {
  if (status === 0) {
    return 'Network error';
  }

  return status >= 500 ? 'Server error' : 'Request failed';
}
