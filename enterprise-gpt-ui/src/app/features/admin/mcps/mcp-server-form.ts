import {
  MCP_AUTH_TYPE,
  McpAuthType,
  McpServerDto,
  McpServerHeaders,
  McpServerWriteBody,
} from '@core/api/mcp.model';

/** Mirrors `CreateMcpServerActionDtoValidator`, so a rejection is the exception. */
export const MCP_SERVER_FIELD_LENGTHS = {
  name: 256,
  description: 1024,
  url: 2048,
  scope: 512,
  iconKey: 64,
} as const;

/** Mirrors `McpServerHeaderRules`, which also sizes the column; change them together. */
export const MCP_HEADER_LIMITS = {
  count: 8,
  name: 64,
  value: 256,
  serialized: 1024,
} as const;

/**
 * Mirrors `McpServerHeaderRules`' reserved set, lowercased: the connection's own, the MCP
 * protocol's (the SDK appends rather than replaces them), and those `HttpRequestHeaders` refuses.
 */
const RESERVED_HEADER_NAMES: readonly string[] = [
  'authorization',
  'proxy-authorization',
  'cookie',
  'set-cookie',
  'host',
  'accept',
  'connection',
  'proxy-connection',
  'keep-alive',
  'transfer-encoding',
  'te',
  'trailer',
  'upgrade',
  'expect',

  'mcp-session-id',
  'mcp-protocol-version',
  'last-event-id',

  'allow',
  'content-disposition',
  'content-encoding',
  'content-language',
  'content-length',
  'content-location',
  'content-md5',
  'content-range',
  'content-type',
  'expires',
  'last-modified',
];

/** Mirrors the server's name rule: ASCII letters, digits and hyphens. */
const HEADER_NAME_PATTERN = /^[A-Za-z0-9-]+$/;

/** Mirrors the server's value rule: printable ASCII, which is the CRLF-injection guard. */
const HEADER_VALUE_PATTERN = /^[\x20-\x7E]+$/;

export interface AuthTypeOption {
  readonly value: McpAuthType;
  readonly label: string;
}

/**
 * No shared "Header key" type: one secret in a plain-text column stays refused, unlike per-user
 * keys stored encrypted. Kept out of `mcp.model.ts` so it stays off the initial graph.
 */
export const AUTH_TYPE_OPTIONS: readonly AuthTypeOption[] = [
  { value: MCP_AUTH_TYPE.none, label: 'None' },
  { value: MCP_AUTH_TYPE.entraIdOnBehalfOf, label: 'Entra ID (on behalf of)' },
  { value: MCP_AUTH_TYPE.userApiKey, label: 'API key (per user)' },
];

/** Falls back to the raw value, so a type added after this build still shows something. */
export function authTypeLabel(authType: number): string {
  return AUTH_TYPE_OPTIONS.find((option) => option.value === authType)?.label ?? String(authType);
}

/** Whether an auth type requires — rather than forbids — a scope. */
export function requiresScope(authType: number): boolean {
  return authType === MCP_AUTH_TYPE.entraIdOnBehalfOf;
}

export function isKnownAuthType(authType: number): authType is McpAuthType {
  return AUTH_TYPE_OPTIONS.some((option) => option.value === authType);
}

export function authTypeError(raw: string): string | null {
  // A row seeded with an unknown type is reachable; without this, Save would stay enabled over
  // a body `toMcpServerBody` refuses.
  return isKnownAuthType(Number(raw)) ? null : 'Choose an auth type.';
}

/**
 * `authType` is a string because a native `<select>` binds strings. No `permissionId`: the API
 * creates and renames the gating permission along with the server.
 */
export interface McpServerFormValue {
  readonly name: string;
  readonly description: string;
  readonly url: string;
  readonly authType: string;
  readonly scope: string;
  /** `''` is "no icon", which the wire carries as null. */
  readonly iconKey: string;
  /** `Name: value` lines; `''` is "no headers", which the wire carries as null. */
  readonly headers: string;
}

/** Keyed in PascalCase, as FluentValidation names the DTO properties. */
const REJECTED_FIELD_LABELS: Readonly<Record<string, string>> = {
  Name: 'name',
  Description: 'description',
  Url: 'URL',
  AuthType: 'auth type',
  Scope: 'scope',
  IconKey: 'icon',
  Headers: 'headers',
};

/** Null when no faulted key is a field shown here, so the caller can use a generic line. */
export function describeRejectedFields(keys: readonly string[]): string | null {
  const labels = [...new Set(keys.map((key) => REJECTED_FIELD_LABELS[key]))].filter(
    (label): label is string => label !== undefined,
  );

  if (labels.length === 0) {
    return null;
  }

  const listed =
    labels.length === 1
      ? labels[0]
      : `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;

  return `The server rejected the ${listed}.`;
}

/** The message the client words for a URL the server's `BeAnAbsoluteHttpUri` refuses. */
export const ABSOLUTE_URL_MESSAGE = 'Enter a full URL beginning with http:// or https://.';

/**
 * Mirrors `BeAnAbsoluteHttpUri`, the only transport constraint: `McpToolProvider` always builds
 * an `HttpClientTransport`.
 */
export function urlError(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return 'A server URL is required.';
  }

  let parsed: URL;
  try {
    // `new URL` with no base is the absolute test: a relative or rooted path throws.
    parsed = new URL(trimmed);
  } catch {
    return ABSOLUTE_URL_MESSAGE;
  }

  return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? null : ABSOLUTE_URL_MESSAGE;
}

/**
 * Both directions, as on the server: required for `EntraIdOnBehalfOf`, empty otherwise. The form
 * hides the field then, so the second arm is a guard rather than a message readers meet.
 */
export function scopeError(raw: string, authType: string): string | null {
  const trimmed = raw.trim();
  const parsed = Number(authType);

  if (!requiresScope(parsed)) {
    return trimmed === '' ? null : 'Only an Entra ID server can carry a scope.';
  }

  return trimmed === '' ? 'A scope is required for an Entra ID server.' : null;
}

/** A parsed header block: the headers to send, and the first reason the text is unusable. */
export interface ParsedHeaders {
  readonly headers: McpServerHeaders;
  readonly error: string | null;
}

/**
 * One textarea, not a row editor: Signal Forms' array support is experimental and
 * `serverMessagesFor` matches flat keys only. The length check underestimates .NET's escaping, so
 * a quote-heavy value can still be refused by the server, whose message renders on this field.
 */
export function parseHeaderLines(raw: string): ParsedHeaders {
  const headers: Record<string, string> = {};
  const seen = new Set<string>();
  const fail = (error: string): ParsedHeaders => ({ headers: {}, error });

  // Trimming each line is also what absorbs the `\r` of a pasted CRLF block.
  const lines = raw
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '');

  if (lines.length > MCP_HEADER_LIMITS.count) {
    return fail(`At most ${MCP_HEADER_LIMITS.count} headers can be configured.`);
  }

  for (const line of lines) {
    const separator = line.indexOf(':');
    if (separator === -1) {
      return fail('Write one header per line, as “Name: value”.');
    }

    const name = line.slice(0, separator).trim();
    const value = line.slice(separator + 1).trim();

    if (name.length > MCP_HEADER_LIMITS.name || !HEADER_NAME_PATTERN.test(name)) {
      return fail(
        `Header names must be ${MCP_HEADER_LIMITS.name} characters or fewer of letters, digits and hyphens.`,
      );
    }

    const lowered = name.toLowerCase();

    if (RESERVED_HEADER_NAMES.includes(lowered)) {
      return fail(`The “${name}” header is set by the API and cannot be configured here.`);
    }

    if (seen.has(lowered)) {
      return fail(`The “${name}” header is listed more than once.`);
    }

    seen.add(lowered);

    if (value === '' || value.length > MCP_HEADER_LIMITS.value) {
      return fail(
        `The value of “${name}” must be between 1 and ${MCP_HEADER_LIMITS.value} characters.`,
      );
    }

    if (!HEADER_VALUE_PATTERN.test(value)) {
      return fail(`The value of “${name}” must be printable ASCII with no line breaks.`);
    }

    headers[name] = value;
  }

  if (JSON.stringify(headers).length > MCP_HEADER_LIMITS.serialized) {
    return fail(
      `The configured headers are too large; they must fit in ${MCP_HEADER_LIMITS.serialized} characters once stored.`,
    );
  }

  return { headers, error: null };
}

export function headersError(raw: string): string | null {
  return parseHeaderLines(raw).error;
}

/** Insertion order, except V8 hoists all-digit names (which the name rule admits) to the front. */
export function formatHeaderLines(headers: McpServerHeaders | null | undefined): string {
  return Object.entries(headers ?? {})
    .map(([name, value]) => `${name}: ${value}`)
    .join('\n');
}

export function toMcpServerFormValue(server: McpServerDto | null): McpServerFormValue {
  if (server === null) {
    return {
      name: '',
      description: '',
      url: '',
      // Pre-selected: `IsInEnum` rejects the `0` an empty select would parse to.
      authType: String(MCP_AUTH_TYPE.none),
      scope: '',
      iconKey: '',
      headers: '',
    };
  }

  return {
    name: server.name,
    description: server.description,
    url: server.url,
    authType: String(server.authType),
    scope: server.scope ?? '',
    // A key this build does not know is seeded through unchanged rather than
    // blanked, so re-saving an untouched row cannot silently drop it.
    iconKey: server.iconKey ?? '',
    headers: formatHeaderLines(server.headers),
  };
}

/**
 * Shared by both verbs: the PUT mapper assigns every property, so a missing field clears it.
 * `scope` goes as null unless required, so a hidden field can never fail the save.
 */
export function toMcpServerBody(value: McpServerFormValue): McpServerWriteBody | null {
  if (urlError(value.url) !== null) {
    return null;
  }

  const authType = Number(value.authType);
  if (!isKnownAuthType(authType)) {
    return null;
  }

  // Only the required direction: a leftover scope on a hidden field is dropped below rather than
  // refused, which would be a silent no-op save.
  if (requiresScope(authType) && scopeError(value.scope, value.authType) !== null) {
    return null;
  }

  const parsedHeaders = parseHeaderLines(value.headers);
  if (parsedHeaders.error !== null) {
    return null;
  }

  const name = value.name.trim();
  const description = value.description.trim();

  if (name === '' || description === '') {
    return null;
  }

  const scope = value.scope.trim();
  const iconKey = value.iconKey.trim();

  return {
    name,
    description,
    url: value.url.trim(),
    authType,
    scope: requiresScope(authType) ? scope : null,
    iconKey: iconKey === '' ? null : iconKey,
    // Never omitted: the PUT is a full representation, so a body without this clears the stored
    // headers — which for a server registered read-only would restore its write tools.
    headers: Object.keys(parsedHeaders.headers).length === 0 ? null : parsedHeaders.headers,
  };
}
