import { describe, expect, it } from 'vitest';
import { AssistantActivity } from '@core/api/assistant-ui-contract';
import { activityLabel } from './activity-label';

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

  // `source` is the raw catalog name; the prefix on the tool name was sanitized by the API.
  it('sanitizes the server name the way the API does before matching', () => {
    expect(activityLabel(activity('GitHub_MCP_create_issue', 'McpTool', 'GitHub MCP'))).toBe(
      'Create issue',
    );
    expect(activityLabel(activity('Jira_Cloud__EU__search', 'McpTool', 'Jira Cloud (EU)'))).toBe(
      'Search',
    );
  });

  // Guards against rewriting sanitize as /\W/g, which would break hyphenated server names.
  it('keeps the characters the API keeps and replaces the ones it replaces', () => {
    expect(activityLabel(activity('My-Server_get_forecast', 'McpTool', 'My-Server'))).toBe(
      'Get forecast',
    );
    // One underscore per UTF-16 code unit, matching a C# iteration over `string`.
    expect(activityLabel(activity('M_t_o_get_forecast', 'McpTool', 'Météo'))).toBe('Get forecast');
  });

  it('does not strip a prefix that differs only in case', () => {
    expect(activityLabel(activity('weather_get_forecast', 'McpTool', 'Weather'))).toBe(
      'Weather get forecast',
    );
  });

  // Real input: the fold falls back to `source` when an event carries no displayName.
  it('leaves a name that is exactly the server name alone', () => {
    expect(activityLabel(activity('Weather', 'McpTool', 'Weather'))).toBe('Weather');
  });

  it('still reads as words when there is no prefix to strip', () => {
    expect(activityLabel(activity('get_forecast', 'McpTool', 'Weather'))).toBe('Get forecast');
    expect(activityLabel(activity('Other_get_forecast', 'McpTool', 'Weather'))).toBe(
      'Other get forecast',
    );
    expect(activityLabel(activity('get_forecast', 'McpTool', undefined))).toBe('Get forecast');
  });

  // "__" becomes spaces, so a guard on the raw remainder would render a blank card.
  it('keeps the whole name when stripping would leave nothing to show', () => {
    expect(activityLabel(activity('Weather_', 'McpTool', 'Weather'))).toBe('Weather');
    expect(activityLabel(activity('Weather___', 'McpTool', 'Weather'))).toBe('Weather');
  });

  it('capitalizes the first character without lowercasing the rest', () => {
    expect(activityLabel(activity('Weather_SEARCH_DOCS', 'McpTool', 'Weather'))).toBe(
      'SEARCH DOCS',
    );
  });

  // The card's own `|| 'this activity'` fallback handles an empty name, so it must stay empty.
  it('returns an empty label only when the display name was already empty', () => {
    expect(activityLabel(activity('', 'McpTool', 'Weather'))).toBe('');
  });

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
