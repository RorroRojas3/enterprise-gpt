import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { COPY_CONFIRMATION_MS } from '@core/clipboard/copy-text';
import { MessageCopy } from './message-copy';

/** Enough fake time for the zoneless scheduler's own notification to land. */
const SCHEDULER_TICK = 20;

/**
 * US-607. The control in isolation: what reaches the clipboard, how long the
 * confirmation lasts, and whether the accessible name keeps up with it.
 */
describe('MessageCopy (US-607)', () => {
  let fixture: ComponentFixture<MessageCopy>;
  let button: HTMLButtonElement;
  let writeText: ReturnType<typeof vi.fn>;
  let clipboardDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    writeText = vi.fn().mockResolvedValue(undefined);
    clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    TestBed.configureTestingModule({});
    fixture = TestBed.createComponent(MessageCopy);
    fixture.componentRef.setInput('text', 'the source');
    fixture.componentRef.setInput('subject', 'response');
    fixture.detectChanges();
    button = (fixture.nativeElement as HTMLElement).querySelector('button') as HTMLButtonElement;
  });

  afterEach(() => {
    if (clipboardDescriptor !== undefined) {
      Object.defineProperty(navigator, 'clipboard', clipboardDescriptor);
    } else {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
    vi.useRealTimers();
  });

  /**
   * Advances fake time, then waits the zoneless scheduler out. `whenStable` is
   * started *before* the second advance because the scheduler notifies through
   * rAF/setTimeout, which the fake clock holds until advanced — the same shape
   * `transcript.spec.ts` uses, and the reason this never calls `detectChanges()`:
   * a forced render would hide a state change that failed to notify at all.
   */
  async function settle(ms = 0): Promise<void> {
    await vi.advanceTimersByTimeAsync(ms);
    const stable = fixture.whenStable();
    await vi.advanceTimersByTimeAsync(SCHEDULER_TICK);
    await stable;
  }

  async function press(): Promise<void> {
    button.click();
    await settle();
  }

  it('puts the text it was given on the clipboard, untouched', async () => {
    fixture.componentRef.setInput('text', '# Heading\n\n```ts\nconst a = 1;\n```\n');
    await press();

    expect(writeText).toHaveBeenCalledExactlyOnceWith('# Heading\n\n```ts\nconst a = 1;\n```\n');
  });

  it('confirms for the kit’s window and then goes back', async () => {
    await press();
    expect(button.textContent?.trim()).toBe('Copied');

    // Measured against the shared constant rather than a literal, and with a
    // margin either side of it, because `settle` advances the clock a little on
    // its own to let the scheduler run.
    await settle(COPY_CONFIRMATION_MS * 0.75);
    expect(button.textContent?.trim()).toBe('Copied');

    await settle(COPY_CONFIRMATION_MS * 0.5);
    expect(button.textContent?.trim()).toBe('Copy');
  });

  it('keeps the accessible name in step with the visible label', async () => {
    // SC 2.5.3: a speech-input user saying "click Copied" has to have something
    // to activate for the two seconds the label reads that.
    expect(button.getAttribute('aria-label')).toBe('Copy response');

    await press();
    expect(button.getAttribute('aria-label')).toBe('Copied');

    await settle(COPY_CONFIRMATION_MS);
    expect(button.getAttribute('aria-label')).toBe('Copy response');
  });

  it('names its subject, so a prompt and an answer are told apart', async () => {
    fixture.componentRef.setInput('subject', 'prompt');
    await settle();

    // Every message in a transcript offers one of these. "Copy" alone leaves a
    // rotor listing a column of identical buttons.
    expect(button.getAttribute('aria-label')).toBe('Copy prompt');
  });

  it('restarts the window rather than letting the first timer end the second', async () => {
    await press();
    await settle(COPY_CONFIRMATION_MS * 0.6);
    await press();

    await settle(COPY_CONFIRMATION_MS * 0.6);
    expect(button.textContent?.trim()).toBe('Copied');
  });

  it('leaves the control alone when the clipboard refuses', async () => {
    writeText.mockRejectedValue(new Error('denied'));
    await press();

    expect(button.textContent?.trim()).toBe('Copy');
    expect(button.getAttribute('aria-label')).toBe('Copy response');
  });

  it('leaves the control alone where there is no clipboard at all', async () => {
    // An insecure context has no `navigator.clipboard` object to call, and that
    // has to fail the same way a refusal does rather than throwing.
    Reflect.deleteProperty(navigator, 'clipboard');
    await press();

    expect(button.textContent?.trim()).toBe('Copy');
  });

  it('cancels the confirmation window when it is destroyed', async () => {
    await press();
    expect(vi.getTimerCount()).toBe(1);

    fixture.destroy();

    // Asserted *before* advancing: advancing would fire the timer and leave the
    // count at zero whether or not the teardown had cancelled it.
    expect(vi.getTimerCount()).toBe(0);
  });

  it('schedules nothing when it is destroyed before the clipboard settles', async () => {
    // The gap between the click and the promise resolving has no timer to cancel
    // yet, so `_clearTimer` alone does not cover it.
    button.click();
    fixture.destroy();
    await vi.advanceTimersByTimeAsync(0);

    expect(vi.getTimerCount()).toBe(0);
  });

  it('is a natively focusable control, so the reveal on focus has something to reveal', () => {
    // The `:focus-within` rule that reveals the footer is CSS jsdom cannot
    // evaluate; what a spec can pin is that the thing it reveals is reachable at
    // all. A `tabindex` here, or a `div` in place of the button, breaks the
    // criterion silently.
    expect(button.tagName).toBe('BUTTON');
    expect(button.getAttribute('tabindex')).toBeNull();
    expect(button.disabled).toBe(false);
  });
});
