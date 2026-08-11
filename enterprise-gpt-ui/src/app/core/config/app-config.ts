/**
 * Runtime configuration, fetched from `config.json` before the application
 * bootstraps.
 *
 * Nothing environment-specific is compiled into the bundle: one build artifact is
 * promoted across environments and `config.json` is overwritten per environment.
 * That is why there is no `environments/` directory and no `fileReplacements`
 * entry in `angular.json`.
 */
export interface AppConfig {
  /** Origin of the Enterprise GPT API, with no trailing slash. */
  readonly apiBaseUrl: string;
  readonly auth: AuthConfig;
  readonly features: FeatureFlags;
}

/**
 * The Entra ID values MSAL needs. Deliberately excludes `cacheLocation`,
 * `allowRedirectInIframe` and the logger options: those are a security posture rather
 * than an environment value, so they are fixed in `buildMsalConfig` where no
 * deployment can weaken them by editing one file.
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

/** Deployment switches for capabilities that are chunked out of the initial bundle. */
export interface FeatureFlags {
  /** Load the diagram renderer chunk. */
  readonly diagrams: boolean;
  /** Load the math renderer chunk. */
  readonly math: boolean;
  /**
   * Read the chat stream as unframed text instead of SSE frames. The fallback for
   * a deployment still running a server that predates the framed contract.
   */
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
 * Validates a parsed `config.json` body.
 *
 * Every failure names the offending field, because that message is what the fatal
 * shell in `index.html` displays to whoever has to fix the deployment.
 *
 * Unknown extra keys are accepted on purpose: a `config.json` written for a newer
 * bundle must still boot an older one.
 *
 * Declared as a `function` rather than an arrow const because TypeScript only
 * honours an `asserts` return type on an explicitly declared call target.
 *
 * @param value The parsed JSON body.
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
}

/**
 * Returns a frozen {@link AppConfig} with `apiBaseUrl` normalized to carry no
 * trailing slash, so callers can join paths without producing a double slash.
 *
 * @param config A value already validated by {@link assertAppConfig}.
 */
export function normalizeAppConfig(config: AppConfig): AppConfig {
  return Object.freeze({
    ...config,
    apiBaseUrl: config.apiBaseUrl.replace(/\/+$/, ''),
    auth: Object.freeze({ ...config.auth, apiScopes: Object.freeze([...config.auth.apiScopes]) }),
    features: Object.freeze({ ...config.features }),
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
