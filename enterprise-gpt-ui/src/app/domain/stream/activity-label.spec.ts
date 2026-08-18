import { describe, expect, it } from 'vitest';
import { activityLabel } from './activity-label';
import { AssistantActivity } from './andes/assistant-ui.contract';

function activity(
  displayName: string,
  kind: AssistantActivity['kind'],
  source?: string,
): Pick<AssistantActivity, 'displayName' | 'kind' | 'source'> {
  return { displayName, kind, source };
}

describe('activityLabel', () => {
  it('strips the leased server prefix and reads the tool as words', () => {
    expect(activityLabel(activity('Weather_get_forecast', 'McpTool', 'Weather'))).toBe(
      'Get forecast',
    );
  });

  // `source` is the catalog name unsanitized; the prefix on the tool name went through the API's
  // sanitizer first, so the two only line up once the same substitution is applied here.
  it('sanitizes the server name the way the API does before matching', () => {
    expect(activityLabel(activity('GitHub_MCP_create_issue', 'McpTool', 'GitHub MCP'))).toBe(
      'Create issue',
    );
    expect(activityLabel(activity('Jira_Cloud__EU__search', 'McpTool', 'Jira Cloud (EU)'))).toBe(
      'Search',
    );
  });

  // The two characters where the API's class and a lazy `\w` disagree. Without these, rewriting
  // sanitize as /\W/g passes every other case here and silently breaks any hyphenated server.
  it('keeps the characters the API keeps and replaces the ones it replaces', () => {
    expect(activityLabel(activity('My-Server_get_forecast', 'McpTool', 'My-Server'))).toBe(
      'Get forecast',
    );
    // One underscore per UTF-16 code unit, matching a C# iteration over `string`.
    expect(activityLabel(activity('M_t_o_get_forecast', 'McpTool', 'Météo'))).toBe('Get forecast');
  });

  // Documented as case-sensitive on purpose: the prefix and `source` come from the same catalog
  // string, so a looser match could only ever remove more than it should.
  it('does not strip a prefix that differs only in case', () => {
    expect(activityLabel(activity('weather_get_forecast', 'McpTool', 'Weather'))).toBe(
      'Weather get forecast',
    );
  });

  // The one production input where an MCP label legitimately *is* the server name: the vendored
  // fold falls back to `source` when an event carries no displayName. A "strip everything before
  // the first underscore" refactor would eat it.
  it('leaves a name that is exactly the server name alone', () => {
    expect(activityLabel(activity('Weather', 'McpTool', 'Weather'))).toBe('Weather');
  });

  // The strip is a refinement, not a precondition: a name that never carried a prefix still has to
  // come out readable, which also makes the function correct if the wire ever sends bare names.
  it('still reads as words when there is no prefix to strip', () => {
    expect(activityLabel(activity('get_forecast', 'McpTool', 'Weather'))).toBe('Get forecast');
    expect(activityLabel(activity('Other_get_forecast', 'McpTool', 'Weather'))).toBe(
      'Other get forecast',
    );
    expect(activityLabel(activity('get_forecast', 'McpTool', undefined))).toBe('Get forecast');
  });

  // Stripping to nothing would leave a card with no label at all, which is worse than a verbose
  // one. The separators-only case is the one that matters: the underscores become spaces, so a
  // guard that judged the raw remainder would call "__" non-empty and render a blank card.
  it('keeps the whole name when stripping would leave nothing to show', () => {
    expect(activityLabel(activity('Weather_', 'McpTool', 'Weather'))).toBe('Weather');
    expect(activityLabel(activity('Weather___', 'McpTool', 'Weather'))).toBe('Weather');
  });

  // Only the first character is touched, so an acronym is not turned into a word.
  it('capitalizes the first character without lowercasing the rest', () => {
    expect(activityLabel(activity('Weather_SEARCH_DOCS', 'McpTool', 'Weather'))).toBe(
      'SEARCH DOCS',
    );
  });

  // The fold can produce an empty display name, and the card's own `|| 'this activity'` fallback is
  // what covers it — this must not turn empty into something that looks like a name.
  it('returns an empty label only when the display name was already empty', () => {
    expect(activityLabel(activity('', 'McpTool', 'Weather'))).toBe('');
  });

  // The kind gate: a function name is a code identifier the design sets in mono, and an agent name
  // is already a display string. Neither carries a server prefix.
  it('leaves every other kind exactly as it arrived', () => {
    expect(activityLabel(activity('search_documents', 'Function', undefined))).toBe(
      'search_documents',
    );
    expect(activityLabel(activity('Forecast Agent', 'Agent', 'agent:forecast'))).toBe(
      'Forecast Agent',
    );
    expect(activityLabel(activity('Thinking it through', 'Unknown', undefined))).toBe(
      'Thinking it through',
    );
  });
});
