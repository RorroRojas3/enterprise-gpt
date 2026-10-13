import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole } from '@testing-library/dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { AppError } from '@core/errors/app-error';
import { buildAppError } from '@core/errors/build-app-error';
import { AnyProblemDetails } from '@core/errors/problem-details';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
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
    const element = host.firstElementChild as HTMLElement | null;
    expect(element).not.toBeNull();
    return element as HTMLElement;
  }

  function iconHrefs(): string[] {
    return [...host.querySelectorAll('use')].map((use) => use.getAttribute('href') ?? '');
  }

  function retry(): HTMLButtonElement | null {
    return queryByRole<HTMLButtonElement>(host, 'button', { name: 'Retry' });
  }

  describe('the conversation-busy warning', () => {
    const busy: TurnNotice = { kind: 'error', error: errorFrom(PROBLEM_FIXTURES.conversationBusy) };

    it('renders the warning panel as a single hourglass row with the frame copy', async () => {
      await render(busy, true);

      expect(card().classList).toContain('alert-warning');
      // The one-line form: Retry shares the message's row.
      expect(card().classList).toContain('d-flex');
      // A sentence rather than a heading, so it keeps body weight.
      const title = getByText(host, 'Another response is still running in this conversation.');
      expect(title.classList).not.toContain('fw-semibold');
      expect(iconHrefs()).toContain('#bi-hourglass-split');
    });

    it('shows nothing that implies the app is waiting on something', async () => {
      await render(busy, true);

      // No spinner, body or trace: nothing is awaited, and a second tab holding the lock is
      // not a defect.
      expect(host.querySelector('.spinner-border')).toBeNull();
      expect(host.querySelectorAll('p')).toHaveLength(0);
    });

    it('offers a manual Retry, drawn without an icon as the row variant is', async () => {
      await render(busy, true);

      expect(retry()?.textContent?.trim()).toBe('Retry');
      expect(retry()?.disabled).toBe(false);
      expect(iconHrefs()).not.toContain('#bi-arrow-clockwise');
    });

    it('emits on Retry so the caller can re-send the turn', async () => {
      await render(busy, true);
      let retries = 0;
      fixture.componentInstance.retried.subscribe(() => (retries += 1));

      getByRole(host, 'button', { name: 'Retry' }).click();

      expect(retries).toBe(1);
    });
  });

  describe('the other frame 1h variants', () => {
    it('keeps the cut-off warning stacked, with its icon on the Retry', async () => {
      await render({ kind: 'cut-off' }, true);

      expect(card().classList).toContain('alert-warning');
      expect(card().classList).not.toContain('d-flex');
      expect(getByText(host, 'This response was cut off').classList).toContain('fw-semibold');
      expect(iconHrefs()).toContain('#bi-arrow-clockwise');
    });

    it('keeps the unreachable tool server on a plain surface card', async () => {
      await render(
        { kind: 'error', error: errorFrom(PROBLEM_FIXTURES.mcpServerUnavailable) },
        true,
      );

      expect(card().classList).not.toContain('alert-warning');
      expect(card().classList).toContain('card');
      expect(getByText(host, 'Weather is unavailable').classList).toContain('text-danger-emphasis');
    });

    it('withholds Retry entirely when the caller does not offer one', async () => {
      await render({ kind: 'error', error: errorFrom(PROBLEM_FIXTURES.conversationBusy) }, false);

      expect(retry()).toBeNull();
    });
  });
});
