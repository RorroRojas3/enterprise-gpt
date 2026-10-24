import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Ridgeline, RidgelineVariant } from './ridgeline';

describe('Ridgeline', () => {
  async function render(variant?: RidgelineVariant) {
    const fixture = TestBed.createComponent(Ridgeline);
    if (variant) {
      fixture.componentRef.setInput('variant', variant);
    }
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it.each([
    ['divider', '0 0 228 10', 1],
    ['thinking', '0 0 76 16', 2],
    ['empty', '0 0 170 40', 2],
    ['auth', '0 0 190 22', 2],
  ] as const)('draws the board geometry for %s', async (variant, viewBox, paths) => {
    const host = await render(variant);
    const svg = host.querySelector('svg');

    expect(svg?.getAttribute('viewBox')).toBe(viewBox);
    expect(host.querySelectorAll('path')).toHaveLength(paths);
  });

  it('defaults to the empty-state variant', async () => {
    const host = await render();

    expect(host.querySelector('svg')?.getAttribute('viewBox')).toBe('0 0 170 40');
  });

  it('animates only the thinking variant, and only its dashed overlay', async () => {
    const thinking = await render('thinking');
    const dashed = thinking.querySelector('.ridge__dash');

    expect(dashed?.getAttribute('stroke-dasharray')).toBe('34 78');
    expect((await render('empty')).querySelector('.ridge__dash')).toBeNull();
  });

  it('is decorative — the surrounding copy carries the meaning', async () => {
    const host = await render('auth');

    expect(host.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});
