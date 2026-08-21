import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { MESSAGE_FEEDBACK_RATING, MessageFeedbackRating } from '@domain/api/conversation';
import { MessageFeedback } from './message-feedback';

/**
 * US-1103. The control in isolation: what it reports, what it announces, and what it
 * refuses. Nothing here decides what a press *means* — that is the store's, and the
 * distinction is the reason this component holds no state at all.
 */
describe('MessageFeedback (US-1103)', () => {
  let fixture: ComponentFixture<MessageFeedback>;
  let host: HTMLElement;
  let emitted: MessageFeedbackRating[];

  function thumbs(): HTMLButtonElement[] {
    return [...host.querySelectorAll('button')];
  }

  function render(rating: MessageFeedbackRating | null = null, pending = false): void {
    fixture.componentRef.setInput('rating', rating);
    fixture.componentRef.setInput('pending', pending);
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
    fixture = TestBed.createComponent(MessageFeedback);
    host = fixture.nativeElement as HTMLElement;
    emitted = [];
    fixture.componentInstance.rated.subscribe((rating) => emitted.push(rating));
    render();
  });

  it('offers both thumbs, named for what they mean rather than for the glyph', () => {
    const [up, down] = thumbs();

    expect(thumbs()).toHaveLength(2);
    expect(up?.getAttribute('aria-label')).toBe('Helpful');
    expect(down?.getAttribute('aria-label')).toBe('Not helpful');
  });

  /**
   * The controls carry no visible label, so `aria-pressed` is the only place the state
   * can live — the same reasoning as the library's Favourites toggle. A name that also
   * changed with the state would announce it twice.
   */
  it('carries the state on aria-pressed, on exactly one thumb at a time', () => {
    render(MESSAGE_FEEDBACK_RATING.up);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['true', 'false']);

    render(MESSAGE_FEEDBACK_RATING.down);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['false', 'true']);

    render(null);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['false', 'false']);
  });

  /**
   * The pressed state has to survive greyscale, which colour alone does not: in dark
   * theme `--muted` and `--brand` sit at a 1.06:1 luminance ratio, so a rated thumb and
   * an unrated one would be the same mark to anyone not reading hue (SC 1.4.1). The glyph
   * is the signal; the colour reinforces it and `aria-pressed` announces it.
   */
  it('fills the selected thumb, so the state is not carried by colour alone', () => {
    const glyphs = (): (string | null)[] =>
      [...host.querySelectorAll('app-icon use')].map((use) => use.getAttribute('href'));

    expect(glyphs()).toEqual(['#bi-hand-thumbs-up', '#bi-hand-thumbs-down']);

    render(MESSAGE_FEEDBACK_RATING.up);
    expect(glyphs()).toEqual(['#bi-hand-thumbs-up-fill', '#bi-hand-thumbs-down']);

    render(MESSAGE_FEEDBACK_RATING.down);
    expect(glyphs()).toEqual(['#bi-hand-thumbs-up', '#bi-hand-thumbs-down-fill']);
  });

  it('reports which thumb was pressed', () => {
    thumbs()[0]?.click();
    thumbs()[1]?.click();

    expect(emitted).toEqual([MESSAGE_FEEDBACK_RATING.up, MESSAGE_FEEDBACK_RATING.down]);
  });

  /**
   * A press on the selected thumb is a withdrawal, and it is the store that says so —
   * this reports the press either way. Swallowing it here would make the rating
   * unwithdrawable and look, from the outside, exactly like a dead button.
   */
  it('reports a press on the thumb that is already selected', () => {
    render(MESSAGE_FEEDBACK_RATING.up);

    thumbs()[0]?.click();

    expect(emitted).toEqual([MESSAGE_FEEDBACK_RATING.up]);
  });

  /**
   * `aria-disabled` and a guard in the handler, never the native attribute: a natively
   * disabled button blurs itself the instant it is pressed, which here would strand a
   * keyboard reader on `<body>`, collapse the footer's `:focus-within` reveal mid-request,
   * and announce the `aria-pressed` transition to nobody — the control's only feedback
   * channel, since it carries no visible text.
   */
  it('refuses both thumbs while a rating for this answer is in flight', () => {
    render(MESSAGE_FEEDBACK_RATING.up, true);

    for (const thumb of thumbs()) {
      expect(thumb.getAttribute('aria-disabled')).toBe('true');
      expect(thumb.disabled).toBe(false);
    }

    thumbs()[1]?.click();
    expect(emitted).toEqual([]);
  });

  it('keeps the refused control focusable, so the press does not throw focus away', () => {
    render(MESSAGE_FEEDBACK_RATING.up, true);
    const thumb = thumbs()[0];

    thumb?.focus();
    thumb?.click();

    expect(document.activeElement).toBe(thumb);
  });

  it('carries no aria-disabled at rest', () => {
    render(MESSAGE_FEEDBACK_RATING.up);

    expect(thumbs().every((thumb) => thumb.getAttribute('aria-disabled') === null)).toBe(true);
  });

  /**
   * The footer is revealed by `:has()` on this class, and a rating nobody can see at rest
   * is not a state that visibly survived anything. The CSS is what jsdom cannot evaluate;
   * the hook it keys on is what a spec can pin.
   */
  it('marks itself rated, so the footer can stay up without a hover', () => {
    expect(host.classList).not.toContain('message-feedback--rated');

    render(MESSAGE_FEEDBACK_RATING.down);
    expect(host.classList).toContain('message-feedback--rated');
  });

  /**
   * The reveal is opacity-based, so these are focusable before they are visible. A
   * `tabindex` here, or a `div` in place of a button, breaks that silently.
   */
  it('is two natively focusable controls, so the reveal on focus has something to reveal', () => {
    for (const thumb of thumbs()) {
      expect(thumb.tagName).toBe('BUTTON');
      expect(thumb.getAttribute('type')).toBe('button');
      expect(thumb.getAttribute('tabindex')).toBeNull();
      expect(thumb.disabled).toBe(false);
    }
  });
});
