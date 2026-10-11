/**
 * Runtime configuration fetched from `config.json` before bootstrap, so one build artifact is
 * promoted across environments (hence no `environments/` or `fileReplacements`).
 */
export interface AppConfig {
  /** Origin of the Enterprise GPT API, with no trailing slash. */
  readonly apiBaseUrl: string;
  readonly auth: AuthConfig;
  readonly features: FeatureFlags;
  /** Absent switches browser telemetry off; nothing is loaded and nothing is sent. */
  readonly telemetry?: TelemetryConfig;
}

/**
 * Excludes `cacheLocation`, `allowRedirectInIframe` and the logger options: they are security
 * posture, fixed in `buildMsalConfig` so no deployment can weaken them by editing one file.
 */
export interface AuthConfig {
  /** Application (client) id of the SPA's Entra ID app registration. */
  readonly clientId: string;
  /** Full authority URL, including the tenant id. */
  readonly authority: string;
  /** Redirect target for the sign-in response; relative to the app origin. */
  readonly redirectUri: string;
  /** Redirect target after sign-out; relative to the app origin. */
  readonly postLogoutRedirectUri: string;
  /** Scopes requested for the API access token. At least one is required. */
  readonly apiScopes: readonly string[];
}

/** Application Insights, for the browser half of a request's telemetry. */
export interface TelemetryConfig {
  /**
   * Public once shipped, and browser ingestion cannot use Entra auth, so name a resource whose
   * open ingestion is acceptable, separate from the API's.
   */
  readonly connectionString: string;
}

/** Deployment switches for capabilities that are chunked out of the initial bundle. */
export interface FeatureFlags {
  /** Load the diagram renderer chunk. */
  readonly diagrams: boolean;
  /** Load the math renderer chunk. */
  readonly math: boolean;
  /** Read the stream as unframed text, for a server that predates the SSE-framed contract. */
  readonly rawStreamCodec: boolean;
}

/** Raised when `config.json` is unreachable, unparseable, or fails validation. */
export class AppConfigError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = 'AppConfigError';
  }
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Each failure names the field, since the fatal shell displays the message. Unknown keys pass so
 * a newer `config.json` still boots an older bundle. A `function` because TypeScript honours
 * `asserts` only on an explicitly declared call target.
 *
 * @throws {AppConfigError} When any field is missing or malformed.
 */
export function assertAppConfig(value: unknown): asserts value is AppConfig {
  const root = requireRecord(value, 'config.json');

  assertHttpUrl(root['apiBaseUrl'], 'apiBaseUrl');

  const auth = requireRecord(root['auth'], 'auth');
  const clientId = auth['clientId'];
  assertNonEmptyString(clientId, 'auth.clientId');
  if (!GUID.test(clientId)) {
    // Caught here rather than at sign-in, where Entra reports it as an opaque
    // "application not found" long after the cause has scrolled away.
    throw new AppConfigError(`"auth.clientId" must be a GUID.`);
  }
  assertHttpUrl(auth['authority'], 'auth.authority', ['https:']);
  assertNonEmptyString(auth['redirectUri'], 'auth.redirectUri');
  assertNonEmptyString(auth['postLogoutRedirectUri'], 'auth.postLogoutRedirectUri');

  const scopes = auth['apiScopes'];
  if (!Array.isArray(scopes) || scopes.length === 0) {
    throw new AppConfigError(`"auth.apiScopes" must be a non-empty array of strings.`);
  }
  scopes.forEach((scope, index) => assertNonEmptyString(scope, `auth.apiScopes[${index}]`));

  const features = requireRecord(root['features'], 'features');
  assertBoolean(features['diagrams'], 'features.diagrams');
  assertBoolean(features['math'], 'features.math');
  assertBoolean(features['rawStreamCodec'], 'features.rawStreamCodec');

  // Optional so older environments keep booting, but validated when present so a half-finished
  // block fails here instead of silently sending nothing.
  if (root['telemetry'] !== undefined) {
    const telemetry = requireRecord(root['telemetry'], 'telemetry');
    assertNonEmptyString(telemetry['connectionString'], 'telemetry.connectionString');
  }
}

/** Returns a frozen copy with no trailing slash on `apiBaseUrl`, so paths join without `//`. */
export function normalizeAppConfig(config: AppConfig): AppConfig {
  return Object.freeze({
    ...config,
    apiBaseUrl: config.apiBaseUrl.replace(/\/+$/, ''),
    auth: Object.freeze({ ...config.auth, apiScopes: Object.freeze([...config.auth.apiScopes]) }),
    features: Object.freeze({ ...config.features }),
    ...(config.telemetry ? { telemetry: Object.freeze({ ...config.telemetry }) } : {}),
  });
}

function requireRecord(value: unknown, field: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new AppConfigError(`"${field}" must be an object.`);
  }

  return value as Record<string, unknown>;
}

function assertNonEmptyString(value: unknown, field: string): asserts value is string {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new AppConfigError(`"${field}" must be a non-empty string.`);
  }
}

function assertHttpUrl(value: unknown, field: string, protocols = ['http:', 'https:']): void {
  assertNonEmptyString(value, field);

  let url: URL;
  try {
    url = new URL(value);
  } catch (cause) {
    throw new AppConfigError(`"${field}" must be an absolute URL.`, { cause });
  }

  if (!protocols.includes(url.protocol)) {
    throw new AppConfigError(`"${field}" must use ${protocols.join(' or ')}.`);
  }
}

function assertBoolean(value: unknown, field: string): void {
  // Rejects the string "false", which is what environment-variable substitution
  // produces when a template forgets to strip the quotes.
  if (typeof value !== 'boolean') {
    throw new AppConfigError(`"${field}" must be a boolean.`);
  }
}
