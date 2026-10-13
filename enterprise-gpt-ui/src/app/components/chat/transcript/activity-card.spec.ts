import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole, queryByText, within } from '@testing-library/dom';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  AssistantActivity,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@shared/models/andes/assistant-ui.contract';
import { iconOf } from '@testing/overlays';
import { assistantEvent } from '@testing/stream-frames';
import { ActivityCard } from './activity-card';

function activity(overrides: Partial<AssistantActivity> = {}): AssistantActivity {
  return {
    scopeId: 'scope-1',
    displayName: 'File Agent',
    kind: 'Agent',
    state: 'Running',
    subStatuses: [],
    children: [],
    ...overrides,
  };
}

describe('ActivityCard', () => {
  let fixture: ComponentFixture<ActivityCard>;
  let host: HTMLElement;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    fixture = TestBed.createComponent(ActivityCard);
    host = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('activity', activity());
    await fixture.whenStable();
  });

  /** The state glyph in the card's own header, ignoring any nested card's. */
  function ownState(card: HTMLElement): string | null {
    return iconOf(card.firstElementChild!);
  }

  it('shows the agent name and its kind as two separate elements', async () => {
    // A pre-composed "Calling File Agent" is the defect the contract keeps these apart to prevent.
    const card = getByRole(host, 'group', { name: 'File Agent' });
    const name = getByText(card, 'File Agent');
    const badge = getByText(card, 'Agent');

    expect(badge.classList.contains('badge')).toBe(true);
    expect(name.contains(badge)).toBe(false);
  });

  // The pair, not the absence: asserting only that the agent avoids the mono styling would still pass
  // if the binding were deleted outright.
  it('sets a function name in mono and leaves an agent name in prose', async () => {
    expect(getByText(host, 'File Agent').classList.contains('font-monospace')).toBe(false);

    fixture.componentRef.setInput(
      'activity',
      activity({ kind: 'Function', displayName: 'get_forecast' }),
    );
    await fixture.whenStable();

    const card = getByRole(host, 'group');
    const name = document.getElementById(card.getAttribute('aria-labelledby') ?? '');
    expect(name?.classList.contains('font-monospace')).toBe(true);
  });

  it('fills the wait before the first child with its own running state', async () => {
    // Nothing invents a sub-status to cover the gap between the call and the first step: the fold
    // seeds an empty list on ActivityStarted and only ActivityProgress adds to it.
    const started = assistantEvent('ActivityStarted', {
      scopeId: 'agent-1',
      depth: 1,
      toolKind: 'Agent',
      displayName: 'File Agent',
    });
    const folded = foldAssistantEvents(createInitialSnapshot(), started).activities[0];

    expect(folded?.subStatuses).toEqual([]);

    fixture.componentRef.setInput('activity', folded);
    await fixture.whenStable();

    // The ring is decoration: the state is the hidden text, and no glyph is drawn yet.
    expect(getByText(host, 'Running').classList.contains('visually-hidden')).toBe(true);
    expect(ownState(getByRole(host, 'group'))).toBeNull();
    expect(queryByRole(host, 'listitem')).toBeNull();
  });

  it('nests the steps the agent took inside its own card', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({
        state: 'Completed',
        durationSeconds: 12.4,
        children: [
          activity({ scopeId: 'scope-2', displayName: 'Running code', state: 'Completed' }),
          activity({ scopeId: 'scope-3', displayName: 'Checking notes.docx', state: 'Completed' }),
        ],
      }),
    );
    await fixture.whenStable();

    const parent = getByRole(host, 'group', { name: 'File Agent' });
    const children = within(parent).getAllByRole('group');

    expect(children).toHaveLength(2);
    expect(within(children[0]!).getByText('Running code')).not.toBeNull();
    expect(within(children[1]!).getByText('Checking notes.docx')).not.toBeNull();
  });

  it('draws a nested step one level in, so a subtree stays readable', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({ children: [activity({ scopeId: 'scope-2', displayName: 'Running code' })] }),
    );
    await fixture.whenStable();

    // The depth has no ARIA state; the stepped-down fill is the visual contract.
    const child = within(getByRole(host, 'group', { name: 'File Agent' })).getByRole('group', {
      name: 'Running code',
    });
    expect(child.classList.contains('bg-body-tertiary')).toBe(true);
    expect(getByRole(host, 'group', { name: 'File Agent' }).classList).toContain('bg-surface');
  });

  it('shows how long a settled activity took', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({ state: 'Completed', durationSeconds: 3.14 }),
    );
    await fixture.whenStable();

    expect(getByText(host, '3.1s').classList.contains('font-monospace')).toBe(true);
    expect(getByText(host, 'Completed after 3.1s').classList.contains('visually-hidden')).toBe(
      true,
    );
  });

  /** The wording is the API's; that the four differ is asserted in `FileAgentFailuresTests`. */
  it('renders a failure with the reason it was given', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({
        state: 'Failed',
        durationSeconds: 8,
        subStatuses: [{ message: 'The file did not open when checked' }],
      }),
    );
    await fixture.whenStable();

    expect(ownState(getByRole(host, 'group'))).toBe('bi-exclamation-triangle-fill');
    expect(getByRole(host, 'listitem').textContent).toContain('The file did not open when checked');
    expect(getByText(host, 'Failed after 8.0s')).not.toBeNull();
  });

  // A refusal is an answer, not a failure: it completes with its reason on the card, and the assistant
  // relays it. Rendering it failed would tell the user the platform broke when it did not.
  it('renders a refusal as a completed activity carrying its reason', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({
        state: 'Completed',
        durationSeconds: 0.4,
        subStatuses: [{ message: 'pptx to pdf is not supported' }],
      }),
    );
    await fixture.whenStable();

    expect(ownState(getByRole(host, 'group'))).toBe('bi-check-circle-fill');
    expect(getByRole(host, 'listitem').textContent).toContain('pptx to pdf is not supported');
  });

  it('offers no cost control on an activity that has reported nothing to show', async () => {
    expect(queryByRole(host, 'button', { name: /^Cost of/ })).toBeNull();
    expect(queryByText(host, /^duration/)).toBeNull();
  });
});
