import { PROBLEM_TYPE } from './problem-types';

/**
 * An RFC 9457 problem body as this API writes it. `traceId` is body-only: the CORS policy
 * exposes no header but `Content-Disposition`.
 */
export interface ProblemDetails {
  /** Application problem type URI, or an RFC 9110 status-section link. */
  readonly type: string;
  /** The API always writes one, but RFC 9457 makes it optional and a body may lack it. */
  readonly title?: string;
  readonly status: number;
  /** Null on validation problems by design: the `errors` map carries the messages. */
  readonly detail?: string | null;
  /** The request path. Carries no query string, so it cannot rebuild the request. */
  readonly instance?: string | null;
  /** W3C trace id, or the hosting layer's request identifier when no activity exists. */
  readonly traceId?: string | null;
}

/**
 * A 400 from FluentValidation. `errors` keys are the server's property paths, left PascalCase by
 * the serializer (`Chunking.MaxTokens`, `Files[0].FileName`); object-level rules use `''`.
 */
export interface ValidationProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.validationError;
  readonly errors: Readonly<Record<string, readonly string[]>>;
}

/** A 400 — not a 413 — from `MaxUploadSizeEndpointFilter`. */
export interface UploadTooLargeProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.uploadTooLarge;
  readonly maxBytes: number;
}

/** A 403 from `PermissionEndpointFilter`. Carries display names, not permission ids. */
export interface PermissionRequiredProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.permissionRequired;
  readonly permissions: readonly string[];
}

/** A 502 raised when an MCP server could not be reached. */
export interface McpServerUnavailableProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.mcpServerUnavailable;
  readonly serverName: string;
}

/**
 * A 428 raised when a tool server needs a user-supplied API key and none is usable. The id
 * addresses the key dialog; the name is display copy.
 */
export interface McpCredentialRequiredProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.mcpCredentialRequired;
  readonly mcpServerId: string;
  readonly serverName: string;
}

/** A 428 raised when a tool server refused the API key the user stored. */
export interface McpCredentialRejectedProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.mcpCredentialRejected;
  readonly mcpServerId: string;
  readonly serverName: string;
}

/** A 503 raised when the selected model's provider has no chat client configured. */
export interface ProviderNotConfiguredProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.providerNotConfigured;
  readonly providerId: string;
}

/**
 * A 503 raised when this deployment has no renderer for an export format. `format` lets a
 * download menu disable that one item rather than the whole control.
 */
export interface ExportRendererNotConfiguredProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.exportRendererNotConfigured;
  readonly format: string;
}

/**
 * Every problem body shape; the trailing base covers framework problems. It does not narrow on
 * `type` (the base's `string` absorbs every literal), so read extensions through the narrowed
 * {@link AppError} arm or the validating readers below.
 */
export type AnyProblemDetails =
  | ValidationProblemDetails
  | UploadTooLargeProblemDetails
  | PermissionRequiredProblemDetails
  | McpServerUnavailableProblemDetails
  | McpCredentialRequiredProblemDetails
  | McpCredentialRejectedProblemDetails
  | ProviderNotConfiguredProblemDetails
  | ExportRendererNotConfiguredProblemDetails
  | ProblemDetails;

/**
 * Null for anything that is not a problem body: an HTML error page, an empty body, or
 * `HttpClient`'s `{ error, text }` wrapper for an unparseable one.
 */
export function parseProblemDetails(value: unknown): AnyProblemDetails | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return null;
  }

  const candidate = value as Record<string, unknown>;
  if (typeof candidate['type'] !== 'string' || typeof candidate['status'] !== 'number') {
    return null;
  }

  return candidate as unknown as AnyProblemDetails;
}

/** Reads a string extension member by its wire name; null when absent or mistyped. */
export function stringExtension(problem: ProblemDetails, key: string): string | null {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return typeof value === 'string' ? value : null;
}

/** Reads a finite number extension member by its wire name; null when absent or mistyped. */
export function numberExtension(problem: ProblemDetails, key: string): number | null {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** Reads a string-array extension member by its wire name, dropping non-string items. */
export function stringArrayExtension(problem: ProblemDetails, key: string): readonly string[] {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [];
}

/** Reads a validation problem's `errors`, dropping malformed entries. */
export function errorsExtension(problem: ProblemDetails): Readonly<Record<string, string[]>> {
  const value = (problem as unknown as Record<string, unknown>)['errors'];
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return {};
  }

  const errors: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(value as Record<string, unknown>)) {
    if (Array.isArray(messages)) {
      errors[key] = messages.filter((message): message is string => typeof message === 'string');
    }
  }

  return errors;
}
