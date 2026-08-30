import {
  MCP_AUTH_TYPE,
  McpAuthType,
  McpServerDto,
  McpServerHeaders,
  McpServerWriteBody,
} from '@domain/api/mcp';

/** Mirrors `CreateMcpServerActionDtoValidator`, so a rejection is the exception. */
export const MCP_SERVER_FIELD_LENGTHS = {
  name: 256,
  description: 1024,
  url: 2048,
  scope: 512,
  iconKey: 64,
} as const;

/**
 * Mirrors `McpServerHeaderRules`, which is the source of truth — that C# file also sizes the
 * column, so these numbers only move together with it.
 */
export const MCP_HEADER_LIMITS = {
  count: 8,
  name: 64,
  value: 256,
  serialized: 1024,
} as const;

/**
 * The header names a registration may not set, lowercased for the case-insensitive comparison
 * HTTP header names require. Mirrors `McpServerHeaderRules`'s reserved set.
 *
 * Three groups, per that file: the connection's own — `Authorization` is the on-behalf-of bearer,
 * and the transport-control names silently change how the connection behaves for every user of
 * the server; the MCP protocol's, which the SDK sets and *appends* rather than replaces, so a
 * stored one would ride alongside the real one; and the content headers, which
 * `HttpRequestHeaders` refuses outright.
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

/** One entry of the form's Auth type select (frame `5h`). */
export interface AuthTypeOption {
  readonly value: McpAuthType;
  readonly label: string;
}

/**
 * The auth types an MCP server can be registered with.
 *
 * Still not the board's three. Frame `5h` draws "Header key", "OAuth2" and "None", and
 * neither of the first two exists: "Header key" was one shared secret an administrator
 * typed into a plain-text column, and it stays refused. `API key (per user)` is the
 * opposite arrangement — each user supplies their own token, stored encrypted, returned
 * by no route — which is why it is here and that one is not. The labels are display copy
 * rather than the enum's `nameof` spelling, for the reason `PROVIDER_OPTIONS` gives.
 *
 * Built here rather than beside the constant in `domain/api/mcp.ts` so it stays off the
 * initial graph: that file is reached by the chat composer's Tools menu, and this list
 * has exactly one consumer, on a lazy admin route.
 */
export const AUTH_TYPE_OPTIONS: readonly AuthTypeOption[] = [
  { value: MCP_AUTH_TYPE.none, label: 'None' },
  { value: MCP_AUTH_TYPE.entraIdOnBehalfOf, label: 'Entra ID (on behalf of)' },
  { value: MCP_AUTH_TYPE.userApiKey, label: 'API key (per user)' },
];

/**
 * The label for an auth type, or the raw value for one this build does not know.
 *
 * Degrades rather than blanking, exactly as `providerMeta` does: a value seeded or
 * added server-side after this build still says *something* in its column.
 */
export function authTypeLabel(authType: number): string {
  return AUTH_TYPE_OPTIONS.find((option) => option.value === authType)?.label ?? String(authType);
}

/** Whether an auth type requires — rather than forbids — a scope. */
export function requiresScope(authType: number): boolean {
  return authType === MCP_AUTH_TYPE.entraIdOnBehalfOf;
}

/**
 * Whether a numeric auth type is one the wire would take.
 *
 * A type predicate rather than a boolean, so {@link toMcpServerBody} narrows to
 * {@link McpAuthType} instead of asserting its way past `number`.
 */
export function isKnownAuthType(authType: number): authType is McpAuthType {
  return AUTH_TYPE_OPTIONS.some((option) => option.value === authType);
}

/** The message for an auth type the wire would refuse, or null when it would take it. */
export function authTypeError(raw: string): string | null {
  // `IsInEnum` refuses anything outside the three, the `0` an empty select parses to
  // included — and a row seeded with a fourth value is reachable, which is why the table
  // has `authTypeLabel`. Without this rule an edit of such a row would leave Save enabled
  // over a body `toMcpServerBody` refuses, and clicking it would do nothing.
  return isKnownAuthType(Number(raw)) ? null : 'Choose an auth type.';
}

/**
 * The values the MCP server form collects (frame `5h`).
 *
 * **`authType` is a string**, because a native `<select>` binds strings and coercing it
 * in the template would put a `NaN` in state the moment the control is empty.
 * {@link toMcpServerBody} parses it, and refuses a body it cannot build.
 *
 * There is no `permissionId`: neither action DTO carries one. `McpServerService` creates
 * the gating permission with the server and renames it in step, so frame `5h`'s "Linked
 * permission" select is a control this API has nothing to bind.
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

/**
 * The form's own name for each key the server can fault, for frame `5h`'s panel.
 *
 * PascalCase because that is how FluentValidation keys them — `nameof(McpServer.Name)`
 * and the property names of the two action DTOs.
 */
const REJECTED_FIELD_LABELS: Readonly<Record<string, string>> = {
  Name: 'name',
  Description: 'description',
  Url: 'URL',
  AuthType: 'auth type',
  Scope: 'scope',
  IconKey: 'icon',
  Headers: 'headers',
};

/**
 * Frame `5h`'s "the server rejected the URL" clause, built from the `errors` keys.
 *
 * Null when the server faulted nothing this form displays — an object-level rule, or a
 * key a later API version introduces — so the caller can fall back to a generic line
 * rather than claim a field was rejected that the reader cannot see.
 */
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
 * Validates the server URL, returning the reason it is unusable or null.
 *
 * Mirrors `BeAnAbsoluteHttpUri` — absolute, and `http` or `https` — which is also the
 * only place the transport is constrained: `McpToolProvider` builds an
 * `HttpClientTransport` unconditionally, and there is no stdio option to choose.
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
 * Validates the scope against the auth type, returning the reason it is unusable.
 *
 * The rule runs in **both** directions, because the server's does: a scope is required
 * for `EntraIdOnBehalfOf` and must be *empty* for every other auth type. The form hides
 * the field on those, so the second arm is the guard behind that rather than a message a
 * reader normally meets.
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
 * Parses the Headers field's `Name: value` lines into the object the wire carries.
 *
 * One textarea rather than a repeated-row editor, deliberately. Signal Forms' array support is
 * `@experimental`, and an indexed editor would make the API key its rejections
 * `Headers[0].Name` — which `serverMessagesFor` cannot match, being flat-keys-only by its own
 * doc. The server faults this whole field as `Headers`, so it needs none of that machinery.
 *
 * Reports the **first** problem rather than all of them, as `urlError` and `scopeError` do.
 *
 * The serialized-length check is an estimate, and always the looser of the two: .NET's default
 * encoder escapes more than `JSON.stringify` does — a double quote becomes the six-character
 * `"` where this counts the two of `\"`, and `<`, `>`, `&`, `'` and `+` are escaped too. So a
 * quote-heavy value can pass here and still be refused by the API. The direction is the safe one: the server is
 * authoritative and its message renders on this field, so the disagreement costs a round trip
 * rather than a wrong answer.
 *
 * Values are trimmed, so a stored value with leading or trailing spaces — which the API accepts,
 * and which this form can therefore never create — is normalized the first time such a row is
 * saved from this dialog.
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

/** Validates the Headers field, returning the first reason it is unusable or null. */
export function headersError(raw: string): string | null {
  return parseHeaderLines(raw).error;
}

/**
 * Renders stored headers back into the field's `Name: value` lines.
 *
 * `Object.entries` order, which is insertion order for every name that is not all digits —
 * V8 hoists integer-like keys to the front. The mirrored name rule admits `123`, so the order is
 * stable rather than guaranteed. Reordering is harmless on the wire; it would only surprise a
 * reader who re-opened such a row.
 */
export function formatHeaderLines(headers: McpServerHeaders | null | undefined): string {
  return Object.entries(headers ?? {})
    .map(([name, value]) => `${name}: ${value}`)
    .join('\n');
}

/** Seeds the form from a server, or from nothing when one is being registered. */
export function toMcpServerFormValue(server: McpServerDto | null): McpServerFormValue {
  if (server === null) {
    return {
      name: '',
      description: '',
      url: '',
      // Pre-selected rather than blank: `IsInEnum` rejects the `0` an empty select would
      // parse to, and `None` is the type a server registered without Entra ID takes.
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
 * Builds the request body, or null when a field the wire needs is unusable.
 *
 * Both verbs go through here, so neither can drop a field on a full-representation PUT:
 * `FromUpdateMcpServerActionDtoToMcpServer` assigns every property unconditionally, so a
 * body missing one **clears** the stored value.
 *
 * `scope` is sent as `null` for {@link MCP_AUTH_TYPE.none} whatever the field holds,
 * because the validator refuses a non-empty scope there — and a field the form has just
 * hidden must not be able to fail the save from behind it.
 */
export function toMcpServerBody(value: McpServerFormValue): McpServerWriteBody | null {
  if (urlError(value.url) !== null) {
    return null;
  }

  const authType = Number(value.authType);
  if (!isKnownAuthType(authType)) {
    return null;
  }

  // Only the direction the wire can actually take. `scopeError`'s other arm — a scope
  // left behind on the `None` field — is normalized away below rather than refused,
  // because the field it belongs to is hidden by then and a refusal there is a save that
  // does nothing with nothing on screen to explain it.
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
