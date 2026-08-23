import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AppError } from '@core/errors/app-error';
import { AnyProblemDetails } from '@core/errors/problem-details';
import { buildAppError } from '@core/errors/build-app-error';
import { TurnNotice, TurnNoticeCard } from './turn-notice-card';

const STREAM_URL = 'https://api.test/api/conversations/6f9d1c1e/stream';

function errorFrom(problem: AnyProblemDetails): AppError {
  return buildAppError({ status: problem.status, problem, url: STREAM_URL, cause: null });
}

describe('TurnNoticeCard (frame 1h)', () => {
  let fixture: ComponentFixture<TurnNoticeCard>;
  let host: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    fixture = TestBed.createComponent(TurnNoticeCard);
    host = fixture.nativeElement as HTMLElement;
  });

  async function render(notice: TurnNotice, retryable = false): Promise<void> {
    fixture.componentRef.setInput('notice', notice);
    fixture.componentRef.setInput('retryable', retryable);
    await fixture.whenStable();
  }

  function card(): HTMLElement {
    const element = host.querySelector<HTMLElement>('.notice');
    expect(element).not.toBeNull();
    return element as HTMLElement;
  }

  function iconHrefs(): string[] {
    return [...host.querySelectorAll('use')].map((use) => use.getAttribute('href') ?? '');
  }

  function title(): string {
    return host.querySelector('.notice__title')?.textContent?.trim() ?? '';
  }

  describe('the conversation-busy warning (US-408)', () => {
    const busy: TurnNotice = { kind: 'error', error: errorFrom(PROBLEM_FIXTURES.conversationBusy) };

    it('renders the warning panel as a single hourglass row with the frame copy', async () => {
      await render(busy, true);

      expect(card().classList).toContain('notice--warn');
      expect(card().classList).toContain('notice--row');
      expect(title()).toBe('Another response is still running in this conversation.');
      expect(iconHrefs()).toContain('#bi-hourglass-split');
    });

    it('shows nothing that implies the app is waiting on something', async () => {
      await render(busy, true);

      // No spinner and no body: nothing is being awaited, and the one sentence
      // is the whole message. A trace id would frame a busy conversation as a
      // defect rather than a second tab holding the lock.
      expect(host.querySelector('.spinner-border')).toBeNull();
      expect(host.querySelector('.notice__body')).toBeNull();
      expect(host.querySelector('.notice__trace')).toBeNull();
    });

    it('offers a manual Retry, drawn without an icon as the row variant is', async () => {
      await render(busy, true);

      const retry = host.querySelector<HTMLButtonElement>('.notice__retry');
      expect(retry?.textContent?.trim()).toBe('Retry');
      expect(retry?.disabled).toBe(false);
      expect(iconHrefs()).not.toContain('#bi-arrow-clockwise');
    });

    it('emits on Retry so the caller can re-send the turn', async () => {
      await render(busy, true);
      let retries = 0;
      fixture.componentInstance.retried.subscribe(() => (retries += 1));

      host.querySelector<HTMLButtonElement>('.notice__retry')?.click();

      expect(retries).toBe(1);
    });
  });

  describe('the other frame 1h variants', () => {
    it('keeps the cut-off warning stacked, with its icon on the Retry', async () => {
      await render({ kind: 'cut-off' }, true);

      expect(card().classList).toContain('notice--warn');
      expect(card().classList).not.toContain('notice--row');
      expect(title()).toBe('This response was cut off');
      expect(iconHrefs()).toContain('#bi-arrow-clockwise');
    });

    it('keeps the unreachable tool server on a plain surface card', async () => {
      await render(
        { kind: 'error', error: errorFrom(PROBLEM_FIXTURES.mcpServerUnavailable) },
        true,
      );

      expect(card().classList).not.toContain('notice--warn');
      expect(title()).toBe('Weather is unavailable');
      expect(host.querySelector('.notice__title--fail')).not.toBeNull();
    });

    it('withholds Retry entirely when the caller does not offer one', async () => {
      await render({ kind: 'error', error: errorFrom(PROBLEM_FIXTURES.conversationBusy) }, false);

      expect(host.querySelector('.notice__retry')).toBeNull();
    });
  });
});
