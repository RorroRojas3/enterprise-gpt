import { BrandIconName } from './brand-icon-names';

/** One entry of the form's Icon select, and of the lookup the pickers read. */
export interface McpIconOption {
  /** The wire value stored in `McpServer.IconKey`; `''` is "no icon". */
  readonly value: string;
  readonly label: string;
  readonly icon: BrandIconName | null;
}

/**
 * The brand marks a server can be given, keyed by the slug the API stores.
 *
 * **The API validates the shape of this key, not its value.** The artwork lives
 * here, in the sprite, so shipping a new icon is a client deploy and nothing
 * more — an unrecognised key degrades to {@link MCP_FALLBACK_ICON} rather than
 * failing, which is also what a server registered by a newer client looks like
 * to an older one.
 *
 * Lives beside the artwork rather than in `core/catalog/` beside
 * `mcp-server-form.ts`, because dependencies run one way — `core` may not import
 * from `shared`, and this list is typed by {@link BrandIconName}. It is off the
 * initial graph either way, for the reason `AUTH_TYPE_OPTIONS` gives: both
 * consumers — the composer's Tools menu and the admin form — ride lazy chunks,
 * while `domain/api/mcp.ts` is reached from the root `McpCatalogStore`.
 */
export const MCP_ICON_OPTIONS: readonly McpIconOption[] = [
  { value: '', label: 'None', icon: null },
  { value: 'microsoft', label: 'Microsoft', icon: 'brand-microsoft' },
  { value: 'context7', label: 'Context7', icon: 'brand-context7' },
];

/** Drawn for a server with no icon, or one whose key this build does not know. */
export const MCP_FALLBACK_ICON = 'bi-plug' as const;

/**
 * The brand mark for a stored icon key, or `null` to fall back to the generic
 * glyph.
 *
 * @param iconKey The key as the API returned it.
 */
export function mcpBrandIcon(iconKey: string | null | undefined): BrandIconName | null {
  if (!iconKey) {
    return null;
  }

  return MCP_ICON_OPTIONS.find((option) => option.value === iconKey)?.icon ?? null;
}
