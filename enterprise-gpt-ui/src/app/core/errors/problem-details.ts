import { PROBLEM_TYPE } from './problem-types';

/**
 * An RFC 9457 problem body as this API writes it.
 *
 * `traceId` and `instance` are stamped on every problem by
 * `ProblemDetailsRegistration.Enrich`. `traceId` is body-only — the API's CORS
 * policy exposes `Content-Disposition` and nothing else — so there is no header
 * to read it from.
 */
export interface ProblemDetails {
  /** Application problem type URI, or an RFC 9110 status-section link. */
  readonly type: string;
  /**
   * Optional per RFC 9457. This API always writes one, but declaring it required
   * would let a body without it produce a value whose type lies.
   */
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
 * A 400 from FluentValidation.
 *
 * The `errors` keys are the server's own property paths, verbatim and therefore
 * PascalCase — unlike every other property on the wire, because
 * `JsonSerializerDefaults.Web` camel-cases property names but leaves dictionary
 * keys alone. Nested and indexed paths appear as `Chunking.MaxTokens` and
 * `Files[0].FileName`; object-level rules use the empty string.
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
 * A 428 raised when a tool server needs an API key the user supplies and none is usable.
 *
 * `mcpServerId` as well as `serverName`: the client opens the key dialog for that server,
 * and a name is display copy rather than something a request can be addressed to.
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
 * A 503 raised when this deployment has no renderer for a conversation export format.
 *
 * `format` is the `?format=` token the request used, so a download menu can disable the one
 * item rather than the whole control.
 */
export interface ExportRendererNotConfiguredProblemDetails extends ProblemDetails {
  readonly type: typeof PROBLEM_TYPE.exportRendererNotConfigured;
  readonly format: string;
}

/**
 * Every problem body shape.
 *
 * The trailing {@link ProblemDetails} covers framework problems, whose `type` is
 * an RFC 9110 status-section link rather than one of ours.
 *
 * Note this union does **not** narrow on `type`: the base member's `type` is
 * `string`, which absorbs every literal, and TypeScript cannot express "any
 * string except these ten". The union is still worth having — it is what lets a
 * fixture or a caller carry extension members without a cast — but to *read* an
 * extension, either narrow the {@link AppError} arm, which is what stores and
 * toasts consume, or use the readers below, which validate the member's type
 * rather than merely asserting it.
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
 * Minimal shape check for a parsed response body.
 *
 * Returns `null` for anything that is not a problem body — an HTML error page, an
 * empty body, or the `{ error, text }` wrapper `HttpClient` produces when a
 * non-JSON error body fails to parse — so both normalization entry points degrade
 * identically.
 *
 * @param value A parsed JSON body, or any other value.
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

/**
 * Reads an extension member that the API documents as a string.
 *
 * @param problem The problem body.
 * @param key The extension name, exactly as it appears on the wire.
 */
export function stringExtension(problem: ProblemDetails, key: string): string | null {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return typeof value === 'string' ? value : null;
}

/**
 * Reads an extension member that the API documents as a number.
 *
 * @param problem The problem body.
 * @param key The extension name, exactly as it appears on the wire.
 */
export function numberExtension(problem: ProblemDetails, key: string): number | null {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * Reads an extension member that the API documents as an array of strings.
 *
 * @param problem The problem body.
 * @param key The extension name, exactly as it appears on the wire.
 */
export function stringArrayExtension(problem: ProblemDetails, key: string): readonly string[] {
  const value = (problem as unknown as Record<string, unknown>)[key];

  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string')
    : [];
}

/**
 * Reads the `errors` dictionary from a validation problem.
 *
 * @param problem The problem body.
 */
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
