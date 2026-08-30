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
  formatHeaderLines,
  headersError,
  parseHeaderLines,
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
    headers: '',
    ...overrides,
  };
}

describe('MCP server form rules (US-1208)', () => {
  describe('auth types', () => {
    it('offers the three the API has, still not the board’s three', () => {
      // The board draws "Header key", "OAuth2" and "None". "Header key" — one shared
      // secret in a plain-text column — stays refused; the per-user API key is the
      // opposite arrangement and is the third arm here.
      expect(AUTH_TYPE_OPTIONS.map((option) => option.value)).toEqual([1, 2, 3]);
      expect(AUTH_TYPE_OPTIONS.map((option) => option.label)).toEqual([
        'None',
        'Entra ID (on behalf of)',
        'API key (per user)',
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
      expect(requiresScope(MCP_AUTH_TYPE.userApiKey)).toBe(false);
    });

    it('refuses an auth type outside the enum, the empty select included', () => {
      // `IsInEnum` refuses the zero an empty select parses to, and a row seeded with a
      // third value is reachable — the table has `authTypeLabel` precisely for it.
      expect(authTypeError(NONE)).toBeNull();
      expect(authTypeError(ENTRA)).toBeNull();
      expect(authTypeError(String(MCP_AUTH_TYPE.userApiKey))).toBeNull();
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

    it.each([
      ['None', NONE],
      ['API key (per user)', String(MCP_AUTH_TYPE.userApiKey)],
    ])('refuses a scope for %s, the direction the server also enforces', (_label, authType) => {
      // The field is hidden and cleared on a switch, so this arm is the guard behind that
      // rather than a message a reader normally meets.
      expect(scopeError('', authType)).toBeNull();
      expect(scopeError('api://sap/.default', authType)).toBe(
        'Only an Entra ID server can carry a scope.',
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
        headers: '',
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
        headers: '',
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
        headers: null,
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

    it('names the headers field, which the API faults under one flat key', () => {
      expect(describeRejectedFields(['Headers'])).toBe('The server rejected the headers.');
    });
  });

  describe('parseHeaderLines', () => {
    it('reads the remote Azure DevOps set', () => {
      expect(parseHeaderLines('X-MCP-Readonly: true\nX-MCP-Toolsets: repos,wit,wiki')).toEqual({
        headers: { 'X-MCP-Readonly': 'true', 'X-MCP-Toolsets': 'repos,wit,wiki' },
        error: null,
      });
    });

    it('accepts an empty block, and blank lines within one', () => {
      expect(parseHeaderLines('')).toEqual({ headers: {}, error: null });
      expect(parseHeaderLines('\n\n  \n')).toEqual({ headers: {}, error: null });
    });

    it('keeps colons in the value, splitting on the first only', () => {
      // A URL in a header value is the case a naive `split(':')` gets wrong.
      expect(parseHeaderLines('X-Origin: https://example.test:8443/a').headers).toEqual({
        'X-Origin': 'https://example.test:8443/a',
      });
    });

    it('absorbs the CR of a pasted CRLF block', () => {
      expect(parseHeaderLines('X-MCP-Readonly: true\r\nX-MCP-Toolsets: repos').headers).toEqual({
        'X-MCP-Readonly': 'true',
        'X-MCP-Toolsets': 'repos',
      });
    });

    it.each([
      // The connection's own.
      'Authorization',
      'authorization',
      'Cookie',
      'Host',
      // Transport control — accepted by HttpRequestHeaders, so they change the connection
      // silently rather than failing loudly.
      'Connection',
      'Transfer-Encoding',
      'Expect',
      // The MCP protocol's — the SDK appends rather than replaces these.
      'Mcp-Session-Id',
      'mcp-session-id',
      'MCP-Protocol-Version',
      'Last-Event-ID',
      // Content headers, which `HttpRequestHeaders` refuses outright.
      'Content-Type',
      'Content-Encoding',
      'Expires',
      'Allow',
    ])('refuses %s, which something other than the registration owns', (name) => {
      expect(headersError(`${name}: anything`)).not.toBeNull();
    });

    it('refuses a line with no colon', () => {
      expect(headersError('X-MCP-Readonly true')).not.toBeNull();
    });

    it('refuses a malformed name', () => {
      expect(headersError('X MCP Readonly: true')).not.toBeNull();
      expect(headersError('X_MCP_Readonly: true')).not.toBeNull();
      expect(headersError(`${'a'.repeat(65)}: true`)).not.toBeNull();
      expect(headersError(`${'a'.repeat(64)}: true`)).toBeNull();
    });

    it('refuses one header spelled two ways', () => {
      // Two entries here, one header on the wire — which survived would be an accident.
      expect(headersError('X-MCP-Toolsets: repos\nx-mcp-toolsets: wit')).not.toBeNull();
    });

    it('refuses an empty or over-long value', () => {
      expect(headersError('X-MCP-Toolsets:')).not.toBeNull();
      expect(headersError(`X-MCP-Toolsets: ${'a'.repeat(257)}`)).not.toBeNull();
      expect(headersError(`X-MCP-Toolsets: ${'a'.repeat(256)}`)).toBeNull();
    });

    it('refuses a value outside printable ASCII', () => {
      expect(headersError('X-MCP-Toolsets: répos')).not.toBeNull();
    });

    it('refuses more than the maximum, and the maximum itself is fine', () => {
      const line = (i: number) => `X-MCP-Header-${i}: v`;
      expect(headersError([1, 2, 3, 4, 5, 6, 7, 8].map(line).join('\n'))).toBeNull();
      expect(headersError([1, 2, 3, 4, 5, 6, 7, 8, 9].map(line).join('\n'))).not.toBeNull();
    });

    it('refuses a set that would not fit the column', () => {
      const big = [1, 2, 3, 4, 5, 6, 7, 8]
        .map((i) => `X-MCP-Header-${i}: ${'v'.repeat(256)}`)
        .join('\n');
      expect(headersError(big)).not.toBeNull();
    });
  });

  describe('header seeding and body', () => {
    it('round-trips stored headers through the field', () => {
      const server = mcpServerFixture({
        headers: { 'X-MCP-Readonly': 'true', 'X-MCP-Toolsets': 'repos' },
      });

      const seeded = toMcpServerFormValue(server);
      expect(seeded.headers).toBe('X-MCP-Readonly: true\nX-MCP-Toolsets: repos');
      expect(formatHeaderLines(server.headers)).toBe(seeded.headers);
      expect(toMcpServerBody(seeded)?.headers).toEqual(server.headers);
    });

    it('sends null rather than an empty object when the field is blank', () => {
      expect(toMcpServerBody(formValue({ headers: '' }))?.headers).toBeNull();
      expect(toMcpServerBody(formValue({ headers: '\n  \n' }))?.headers).toBeNull();
    });

    it('refuses to build a body the API would reject', () => {
      expect(toMcpServerBody(formValue({ headers: 'Authorization: Bearer x' }))).toBeNull();
      expect(toMcpServerBody(formValue({ headers: 'no colon here' }))).toBeNull();
    });
  });
});
