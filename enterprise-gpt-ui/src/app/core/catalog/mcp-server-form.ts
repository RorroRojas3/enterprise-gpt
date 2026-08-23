import { MCP_AUTH_TYPE, McpAuthType, McpServerDto, McpServerWriteBody } from '@domain/api/mcp';

/** Mirrors `CreateMcpServerActionDtoValidator`, so a rejection is the exception. */
export const MCP_SERVER_FIELD_LENGTHS = {
  name: 256,
  description: 1024,
  url: 2048,
  scope: 512,
  iconKey: 64,
} as const;

/** One entry of the form's Auth type select (frame `5h`). */
export interface AuthTypeOption {
  readonly value: McpAuthType;
  readonly label: string;
}

/**
 * The auth types an MCP server can be registered with.
 *
 * **Two, not frame `5h`'s three.** The board draws "Header key", "OAuth2" and "None";
 * `McpAuthTypes` has `None` and `EntraIdOnBehalfOf`, and neither of the board's first
 * two exists in this system — the same class of board error as frame `4l`'s file
 * extensions. The labels are display copy rather than the enum's `nameof` spelling, for
 * the reason `PROVIDER_OPTIONS` gives.
 *
 * Built here rather than beside the constant in `domain/api/mcp.ts` so it stays off the
 * initial graph: that file is reached by the chat composer's Tools menu, and this list
 * has exactly one consumer, on a lazy admin route.
 */
export const AUTH_TYPE_OPTIONS: readonly AuthTypeOption[] = [
  { value: MCP_AUTH_TYPE.none, label: 'None' },
  { value: MCP_AUTH_TYPE.entraIdOnBehalfOf, label: 'Entra ID (on behalf of)' },
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
  return authType === MCP_AUTH_TYPE.none || authType === MCP_AUTH_TYPE.entraIdOnBehalfOf;
}

/** The message for an auth type the wire would refuse, or null when it would take it. */
export function authTypeError(raw: string): string | null {
  // `IsInEnum` refuses anything outside the two, the `0` an empty select parses to
  // included — and a row seeded with a third value is reachable, which is why the table
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
 * for `EntraIdOnBehalfOf` and must be *empty* for `None`. The form hides the field on
 * `None` and clears it, so the second arm is the guard behind that rather than a message
 * a reader normally meets.
 */
export function scopeError(raw: string, authType: string): string | null {
  const trimmed = raw.trim();
  const parsed = Number(authType);

  if (!requiresScope(parsed)) {
    return trimmed === '' ? null : 'A server with no authentication cannot carry a scope.';
  }

  return trimmed === '' ? 'A scope is required for an Entra ID server.' : null;
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
  };
}
