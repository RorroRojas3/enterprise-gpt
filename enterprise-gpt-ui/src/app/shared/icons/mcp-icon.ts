import { BrandIconName } from './brand-icon-names';

/** One entry of the form's Icon select, and of the lookup the pickers read. */
export interface McpIconOption {
  /** The wire value stored in `McpServer.IconKey`; `''` is "no icon". */
  readonly value: string;
  readonly label: string;
  readonly icon: BrandIconName | null;
}

/**
 * The marks a server can be given, keyed by the stored slug. The API checks only its shape, so
 * an unknown key degrades to {@link MCP_FALLBACK_ICON}. This is the admin `<select>` in source
 * order: "None", then alphabetical.
 */
export const MCP_ICON_OPTIONS: readonly McpIconOption[] = [
  { value: '', label: 'None', icon: null },
  { value: 'azure', label: 'Azure', icon: 'brand-azure' },
  { value: 'context7', label: 'Context7', icon: 'brand-context7' },
  { value: 'github', label: 'GitHub', icon: 'brand-github' },
  { value: 'microsoft', label: 'Microsoft', icon: 'brand-microsoft' },
  { value: 'salesforce', label: 'Salesforce', icon: 'brand-salesforce' },
];

/** Drawn for a server with no icon, or one whose key this build does not know. */
export const MCP_FALLBACK_ICON = 'bi-plug' as const;

/** The brand mark for a stored icon key, or `null` to fall back to the generic glyph. */
export function mcpBrandIcon(iconKey: string | null | undefined): BrandIconName | null {
  if (!iconKey) {
    return null;
  }

  return MCP_ICON_OPTIONS.find((option) => option.value === iconKey)?.icon ?? null;
}
