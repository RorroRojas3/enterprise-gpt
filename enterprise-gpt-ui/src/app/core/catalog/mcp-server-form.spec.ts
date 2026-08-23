import { describe, expect, it } from 'vitest';
import { MCP_AUTH_TYPE } from '@domain/api/mcp';
import { mcpServerFixture } from '@testing/catalog';
import {
  ABSOLUTE_URL_MESSAGE,
  AUTH_TYPE_OPTIONS,
  McpServerFormValue,
  authTypeError,
  authTypeLabel,
  describeRejectedFields,
  requiresScope,
  scopeError,
  toMcpServerBody,
  toMcpServerFormValue,
  urlError,
} from './mcp-server-form';

const NONE = String(MCP_AUTH_TYPE.none);
const ENTRA = String(MCP_AUTH_TYPE.entraIdOnBehalfOf);

function formValue(overrides: Partial<McpServerFormValue> = {}): McpServerFormValue {
  return {
    name: 'SAP Ledger',
    description: 'Cost-center and PO queries for finance',
    url: 'https://mcp.example.test/sap',
    authType: NONE,
    scope: '',
    iconKey: '',
    ...overrides,
  };
}

describe('MCP server form rules (US-1208)', () => {
  describe('auth types', () => {
    it('offers the two the API has, not frame 5h’s three', () => {
      // The board draws "Header key", "OAuth2" and "None". `McpAuthTypes` has None and
      // EntraIdOnBehalfOf, and neither of the first two exists in this system.
      expect(AUTH_TYPE_OPTIONS.map((option) => option.value)).toEqual([1, 2]);
      expect(AUTH_TYPE_OPTIONS.map((option) => option.label)).toEqual([
        'None',
        'Entra ID (on behalf of)',
      ]);
    });

    it('labels an auth type this build does not know by its raw value', () => {
      // Degrades rather than blanking, as the model table's provider dot does: a value
      // added server-side after this build still says something in its column.
      expect(authTypeLabel(MCP_AUTH_TYPE.entraIdOnBehalfOf)).toBe('Entra ID (on behalf of)');
      expect(authTypeLabel(99)).toBe('99');
    });

    it('requires a scope only for the Entra ID arm', () => {
      expect(requiresScope(MCP_AUTH_TYPE.none)).toBe(false);
      expect(requiresScope(MCP_AUTH_TYPE.entraIdOnBehalfOf)).toBe(true);
    });

    it('refuses an auth type outside the enum, the empty select included', () => {
      // `IsInEnum` refuses the zero an empty select parses to, and a row seeded with a
      // third value is reachable — the table has `authTypeLabel` precisely for it.
      expect(authTypeError(NONE)).toBeNull();
      expect(authTypeError(ENTRA)).toBeNull();
      expect(authTypeError('')).toBe('Choose an auth type.');
      expect(authTypeError('0')).toBe('Choose an auth type.');
      expect(authTypeError('99')).toBe('Choose an auth type.');
      expect(authTypeError('abc')).toBe('Choose an auth type.');
    });
  });

  describe('urlError', () => {
    it('requires a URL', () => {
      expect(urlError('')).toBe('A server URL is required.');
      expect(urlError('   ')).toBe('A server URL is required.');
    });

    it('refuses anything that is not an absolute http or https URL', () => {
      // Exactly the four shapes `BeAnAbsoluteHttpUri` rejects.
      expect(urlError('relative/path')).toBe(ABSOLUTE_URL_MESSAGE);
      expect(urlError('/rooted/path')).toBe(ABSOLUTE_URL_MESSAGE);
      expect(urlError('ftp://mcp.example.test/x')).toBe(ABSOLUTE_URL_MESSAGE);
      expect(urlError('not a url')).toBe(ABSOLUTE_URL_MESSAGE);
      // The board's own typo — a comma where the dot belongs — which is why frame `5h`
      // draws this field invalid.
      expect(urlError('mcp.andessoftware,net/sap')).toBe(ABSOLUTE_URL_MESSAGE);
    });

    it('accepts both schemes, and trims', () => {
      expect(urlError('https://mcp.example.test/sap')).toBeNull();
      expect(urlError('http://localhost:3000/mcp')).toBeNull();
      expect(urlError('  https://mcp.example.test/sap  ')).toBeNull();
    });
  });

  describe('scopeError', () => {
    it('requires a scope for Entra ID', () => {
      expect(scopeError('', ENTRA)).toBe('A scope is required for an Entra ID server.');
      expect(scopeError('  ', ENTRA)).toBe('A scope is required for an Entra ID server.');
      expect(scopeError('api://sap/.default', ENTRA)).toBeNull();
    });

    it('refuses a scope for None, which is the direction the server also enforces', () => {
      // The field is hidden and cleared on a switch, so this arm is the guard behind that
      // rather than a message a reader normally meets.
      expect(scopeError('', NONE)).toBeNull();
      expect(scopeError('api://sap/.default', NONE)).toBe(
        'A server with no authentication cannot carry a scope.',
      );
    });
  });

  describe('toMcpServerFormValue', () => {
    it('seeds a create with None pre-selected, because IsInEnum refuses zero', () => {
      const value = toMcpServerFormValue(null);

      expect(value).toEqual({
        name: '',
        description: '',
        url: '',
        authType: NONE,
        scope: '',
        iconKey: '',
      });
    });

    it('seeds an edit from the row, with a null scope as an empty field', () => {
      const server = mcpServerFixture({
        name: 'SAP Ledger',
        description: 'Finance queries',
        url: 'https://mcp.example.test/sap',
        authType: MCP_AUTH_TYPE.entraIdOnBehalfOf,
        scope: 'api://sap/.default',
        iconKey: 'microsoft',
      });

      expect(toMcpServerFormValue(server)).toEqual({
        name: 'SAP Ledger',
        description: 'Finance queries',
        url: 'https://mcp.example.test/sap',
        authType: ENTRA,
        scope: 'api://sap/.default',
        iconKey: 'microsoft',
      });

      expect(toMcpServerFormValue(mcpServerFixture({ scope: null })).scope).toBe('');
      expect(toMcpServerFormValue(mcpServerFixture({ iconKey: null })).iconKey).toBe('');
      // A key this build ships no artwork for still seeds, so re-saving an
      // untouched row cannot silently drop it.
      expect(toMcpServerFormValue(mcpServerFixture({ iconKey: 'future' })).iconKey).toBe('future');
    });
  });

  describe('toMcpServerBody', () => {
    it('round-trips every field the full-representation PUT requires', () => {
      expect(toMcpServerBody(formValue({ authType: ENTRA, scope: 'api://sap/.default' }))).toEqual({
        name: 'SAP Ledger',
        description: 'Cost-center and PO queries for finance',
        url: 'https://mcp.example.test/sap',
        authType: MCP_AUTH_TYPE.entraIdOnBehalfOf,
        scope: 'api://sap/.default',
        iconKey: null,
      });
    });

    it('sends a null icon key for an empty select, and the slug otherwise', () => {
      expect(toMcpServerBody(formValue({ iconKey: '' }))?.iconKey).toBeNull();
      expect(toMcpServerBody(formValue({ iconKey: 'context7' }))?.iconKey).toBe('context7');
    });

    it('sends a null scope for None, whatever the hidden field still holds', () => {
      // Normalized rather than refused. The dialog clears the field on a switch, so this
      // is the belt behind that — and refusing here would be a Save that does nothing,
      // with the field that caused it no longer on screen to explain why.
      expect(
        toMcpServerBody(formValue({ authType: NONE, scope: 'api://sap/.default' }))?.scope,
      ).toBeNull();

      expect(toMcpServerBody(formValue({ authType: NONE, scope: '' }))?.scope).toBeNull();
    });

    it('trims, so a stray space cannot become part of a name or a URL', () => {
      const body = toMcpServerBody(
        formValue({ name: '  SAP Ledger  ', url: '  https://mcp.example.test/sap  ' }),
      );

      expect(body?.name).toBe('SAP Ledger');
      expect(body?.url).toBe('https://mcp.example.test/sap');
    });

    it('refuses a body it cannot build rather than sending a broken one', () => {
      expect(toMcpServerBody(formValue({ name: '   ' }))).toBeNull();
      expect(toMcpServerBody(formValue({ description: '' }))).toBeNull();
      expect(toMcpServerBody(formValue({ url: 'not a url' }))).toBeNull();
      expect(toMcpServerBody(formValue({ authType: ENTRA, scope: '' }))).toBeNull();
      // An auth type outside the enum would fail `IsInEnum`; it never reaches the wire.
      expect(toMcpServerBody(formValue({ authType: '0' }))).toBeNull();
      expect(toMcpServerBody(formValue({ authType: 'abc' }))).toBeNull();
    });
  });

  describe('describeRejectedFields', () => {
    it('words frame 5h’s clause from the errors keys', () => {
      expect(describeRejectedFields(['Url'])).toBe('The server rejected the URL.');
      expect(describeRejectedFields(['Name'])).toBe('The server rejected the name.');
      expect(describeRejectedFields(['Url', 'Scope'])).toBe(
        'The server rejected the URL and scope.',
      );
      expect(describeRejectedFields(['Name', 'Url', 'Scope'])).toBe(
        'The server rejected the name, URL and scope.',
      );
    });

    it('says nothing rather than naming a field the reader cannot find', () => {
      // An object-level rule, or a key a later API version introduces. The caller falls
      // back to a generic line instead of pointing at a control that is not there.
      expect(describeRejectedFields([])).toBeNull();
      expect(describeRejectedFields(['Something'])).toBeNull();
    });
  });
});
