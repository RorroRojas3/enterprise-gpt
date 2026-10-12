import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { EmptyState } from './empty-state';

describe('EmptyState', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('exposes a heading at the level the caller asked for', async () => {
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('heading', 'No conversations match "quarterly"');
    fixture.componentRef.setInput('headingLevel', 2);
    await fixture.whenStable();

    const heading = (fixture.nativeElement as HTMLElement).querySelector('[role="heading"]');
    expect(heading?.getAttribute('aria-level')).toBe('2');
    expect(heading?.textContent).toContain('No conversations match');
  });

  it('can drop the illustration for a compact surface such as the sidebar', async () => {
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('heading', 'Nothing here yet');
    fixture.componentRef.setInput('illustration', 'none');
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('app-ridgeline')).toBeNull();
  });
});
