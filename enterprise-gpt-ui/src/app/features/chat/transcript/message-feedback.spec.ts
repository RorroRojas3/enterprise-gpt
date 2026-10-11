import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { MESSAGE_FEEDBACK_RATING, MessageFeedbackRating } from '@domain/api/conversation';
import { MessageFeedback } from './message-feedback';

describe('MessageFeedback', () => {
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

  /** No visible label, so `aria-pressed` carries the state; a changing name would say it twice. */
  it('carries the state on aria-pressed, on exactly one thumb at a time', () => {
    render(MESSAGE_FEEDBACK_RATING.up);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['true', 'false']);

    render(MESSAGE_FEEDBACK_RATING.down);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['false', 'true']);

    render(null);
    expect(thumbs().map((thumb) => thumb.getAttribute('aria-pressed'))).toEqual(['false', 'false']);
  });

  /** The glyph carries the state: in dark theme the two colours sit at 1.06:1 (SC 1.4.1). */
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

  /** The store reads it as a withdrawal; swallowing it here would make ratings permanent. */
  it('reports a press on the thumb that is already selected', () => {
    render(MESSAGE_FEEDBACK_RATING.up);

    thumbs()[0]?.click();

    expect(emitted).toEqual([MESSAGE_FEEDBACK_RATING.up]);
  });

  /** A natively disabled button would blur on press and collapse the footer's reveal. */
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

  /** jsdom cannot evaluate the footer's `:has()` reveal, so this pins the class it keys on. */
  it('marks itself rated, so the footer can stay up without a hover', () => {
    expect(host.classList).not.toContain('message-feedback--rated');

    render(MESSAGE_FEEDBACK_RATING.down);
    expect(host.classList).toContain('message-feedback--rated');
  });

  /** The reveal is opacity-based, so these must be focusable before they are visible. */
  it('is two natively focusable controls, so the reveal on focus has something to reveal', () => {
    for (const thumb of thumbs()) {
      expect(thumb.tagName).toBe('BUTTON');
      expect(thumb.getAttribute('type')).toBe('button');
      expect(thumb.getAttribute('tabindex')).toBeNull();
      expect(thumb.disabled).toBe(false);
    }
  });
});
