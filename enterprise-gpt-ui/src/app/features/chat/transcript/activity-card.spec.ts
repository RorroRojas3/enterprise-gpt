import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  AssistantActivity,
  createInitialSnapshot,
  foldAssistantEvents,
} from '@domain/stream/andes/assistant-ui.contract';
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

  it('shows the agent name and its kind as two separate elements', async () => {
    // A pre-composed "Calling File Agent" is the defect the contract keeps these apart to prevent.
    const name = host.querySelector('.activity-card__name');

    expect(name?.textContent?.trim()).toBe('File Agent');
    expect(host.querySelector('.kind-badge')?.textContent?.trim()).toBe('Agent');
    expect(name?.querySelector('.kind-badge')).toBeNull();
  });

  // The pair, not the absence: asserting only that the agent avoids the mono styling would still pass
  // if the binding were deleted outright.
  it('sets a function name in mono and leaves an agent name in prose', async () => {
    expect(host.querySelector('.activity-card__name--mono')).toBeNull();

    fixture.componentRef.setInput(
      'activity',
      activity({ kind: 'Function', displayName: 'get_forecast' }),
    );
    await fixture.whenStable();

    expect(host.querySelector('.activity-card__name--mono')).not.toBeNull();
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

    expect(host.querySelector('.activity-card__ring')).not.toBeNull();
    expect(host.querySelector('.activity-card__substatus')).toBeNull();
    expect(
      host.querySelector('.activity-card__header > .visually-hidden')?.textContent?.trim(),
    ).toBe('Running');
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

    const children = [...host.querySelectorAll('.activity-card__children .activity-card__name')];

    expect(children.map((child) => child.textContent?.trim())).toEqual([
      'Running code',
      'Checking notes.docx',
    ]);
  });

  it('draws a nested step one level in, so a subtree stays readable', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({ children: [activity({ scopeId: 'scope-2', displayName: 'Running code' })] }),
    );
    await fixture.whenStable();

    expect(host.querySelector('.activity-card__children .activity-card--depth-1')).not.toBeNull();
  });

  it('shows how long a settled activity took', async () => {
    fixture.componentRef.setInput(
      'activity',
      activity({ state: 'Completed', durationSeconds: 3.14 }),
    );
    await fixture.whenStable();

    expect(host.querySelector('.activity-card__duration')?.textContent?.trim()).toBe('3.1s');
    expect(
      host.querySelector('.activity-card__header > .visually-hidden')?.textContent?.trim(),
    ).toBe('Completed after 3.1s');
  });

  /**
   * The card renders whatever reason it is handed, which is all it can do: progress events carry no
   * error text, so the failure's wording is the API's. That the four wordings differ is asserted where
   * they live — `Enterprise.Gpt.Unit.Test.Agents.FileAgentFailuresTests` — because a component spec
   * would only be comparing strings it supplied itself.
   */
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

    expect(host.querySelector('app-icon.activity-card__state--warn')).not.toBeNull();
    expect(host.querySelector('.activity-card__substatus')?.textContent?.trim()).toBe(
      'The file did not open when checked',
    );
    expect(
      host.querySelector('.activity-card__header > .visually-hidden')?.textContent?.trim(),
    ).toBe('Failed after 8.0s');
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

    expect(host.querySelector('app-icon.activity-card__state--ok')).not.toBeNull();
    expect(host.querySelector('app-icon.activity-card__state--warn')).toBeNull();
    expect(host.querySelector('.activity-card__substatus')?.textContent?.trim()).toBe(
      'pptx to pdf is not supported',
    );
  });

  it('offers no cost control on an activity that has reported nothing to show', async () => {
    expect(host.querySelector('.activity-card__toggle')).toBeNull();
  });
});
