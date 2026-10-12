import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Skeleton } from './skeleton';

describe('Skeleton', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('is hidden from assistive technology', async () => {
    const fixture = TestBed.createComponent(Skeleton);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).getAttribute('aria-hidden')).toBe('true');
  });

  it('renders one bar per line, with the last one short', async () => {
    const fixture = TestBed.createComponent(Skeleton);
    fixture.componentRef.setInput('lines', 3);
    await fixture.whenStable();

    const bars = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.skeleton')];
    expect(bars).toHaveLength(3);
    expect((bars[2] as HTMLElement).style.width).toBe('60%');
  });
});
