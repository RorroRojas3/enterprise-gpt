import { describe, expect, it } from 'vitest';
import { BRAND_ICON_NAME_SET } from './brand-icon-names';
import { MCP_FALLBACK_ICON, MCP_ICON_OPTIONS, mcpBrandIcon } from './mcp-icon';

describe('MCP icon registry (US-418)', () => {
  it('resolves a known key to its mark', () => {
    expect(mcpBrandIcon('microsoft')).toBe('brand-microsoft');
    expect(mcpBrandIcon('context7')).toBe('brand-context7');
    expect(mcpBrandIcon('azure')).toBe('brand-azure');
    expect(mcpBrandIcon('github')).toBe('brand-github');
    expect(mcpBrandIcon('salesforce')).toBe('brand-salesforce');
  });

  it('degrades an absent or unknown key rather than rendering a blank symbol', () => {
    // A key a newer client wrote is the reachable case: the API validates the
    // shape of this value, never its membership, on purpose.
    expect(mcpBrandIcon('future-brand')).toBeNull();
    expect(mcpBrandIcon(null)).toBeNull();
    expect(mcpBrandIcon(undefined)).toBeNull();
    expect(mcpBrandIcon('')).toBeNull();
  });

  it('offers only marks the sprite actually holds, with "None" first', () => {
    expect(MCP_ICON_OPTIONS[0]).toEqual({ value: '', label: 'None', icon: null });

    // Source order is the admin form's <select> order, so the docstring's "file it,
    // don't append it" needs something behind it.
    const labels = MCP_ICON_OPTIONS.slice(1).map((option) => option.label);
    expect(labels).toEqual([...labels].sort());

    for (const option of MCP_ICON_OPTIONS.slice(1)) {
      expect(option.value).toMatch(/^[a-z0-9]+(-[a-z0-9]+)*$/);
      expect(option.value.length).toBeLessThanOrEqual(64);
      expect(option.icon !== null && BRAND_ICON_NAME_SET.has(option.icon)).toBe(true);
    }
  });

  it('falls back to the glyph the admin area already uses for MCP', () => {
    expect(MCP_FALLBACK_ICON).toBe('bi-plug');
  });
});
