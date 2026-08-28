import { ComponentFixture, TestBed } from '@angular/core/testing';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { AssistantActivity } from '@domain/stream/andes/assistant-ui.contract';
import { afterEach, describe, expect, it } from 'vitest';
import { ActivityCard } from './activity-card';

/**
 * The agent's activity subtree, which no route-level audit can reach.
 *
 * Reopening a conversation replays the answer and its files but never the activity tree, so the only
 * transcript a `/chat/:id` audit can flush is one with no cards in it. This mounts the component
 * directly, as the unavailable-panel audit does.
 *
 * The fixture is deliberately wider than what the server sends: it carries `usage`, which the vendored
 * fold never writes today (see `turn-usage.ts`), so that the cost strip is drawn and audited rather
 * than left for whichever story starts populating it. The states are the settled ones — a running
 * card's only distinct element is an `aria-hidden` ring with no text, which no axe rule reaches.
 */
describe('ActivityCard accessibility', () => {
  let fixture: ComponentFixture<ActivityCard> | null = null;

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    clearTheme();
  });

  const AGENT: AssistantActivity = {
    scopeId: 'agent-1',
    displayName: 'File Agent',
    kind: 'Agent',
    state: 'Failed',
    durationSeconds: 41.2,
    usage: { inputTokens: 1204, outputTokens: 88, totalTokens: 1292 },
    subStatuses: [{ message: 'The file did not open when checked' }],
    children: [
      {
        scopeId: 'step-1',
        displayName: 'Preparing files',
        kind: 'Agent',
        state: 'Completed',
        durationSeconds: 1.1,
        subStatuses: [{ message: 'Loaded 1 file(s)' }],
        children: [],
      },
      {
        scopeId: 'step-2',
        displayName: 'Running code',
        kind: 'Agent',
        state: 'Completed',
        durationSeconds: 32.7,
        subStatuses: [],
        children: [
          {
            scopeId: 'step-3',
            displayName: 'Checking the file',
            kind: 'Agent',
            state: 'Failed',
            durationSeconds: 0.4,
            subStatuses: [{ message: 'Checking quarterly-summary.xlsx' }],
            children: [],
          },
        ],
      },
    ],
  };

  for (const theme of THEMES) {
    it(`has no serious or critical violations in the ${theme} theme`, async () => {
      applyTheme(theme);

      fixture = TestBed.createComponent(ActivityCard);
      fixture.componentRef.setInput('activity', AGENT);

      // Attached, because axe reads computed style and a detached element has none.
      document.body.append(fixture.nativeElement as HTMLElement);
      await fixture.whenStable();

      // Open, because the chevron's panel is where its `aria-controls` target lives and a closed one
      // audits neither the relationship nor the strip's own contrast. Asserted rather than assumed:
      // `expandable()` is derived from the fixture's own usage and duration, so a fixture change
      // could quietly leave this auditing a collapsed card and still pass.
      const host = fixture.nativeElement as HTMLElement;
      const toggle = host.querySelector<HTMLButtonElement>('.activity-card__toggle');

      expect(toggle).not.toBeNull();
      toggle!.click();
      await fixture.whenStable();

      expect(toggle!.getAttribute('aria-expanded')).toBe('true');
      expect(host.querySelector('.activity-card__usage')).not.toBeNull();

      await expectNoSeriousViolations(
        fixture.nativeElement as Element,
        `ActivityCard agent subtree (${theme})`,
      );
    });
  }
});
