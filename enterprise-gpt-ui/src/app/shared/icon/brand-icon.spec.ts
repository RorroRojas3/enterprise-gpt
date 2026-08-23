import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BrandIcon } from './brand-icon';
import { BrandIconName } from './brand-icon-names';

describe('BrandIcon (US-418)', () => {
  let fixture: ComponentFixture<BrandIcon>;

  async function render(inputs: { name: BrandIconName; label?: string }) {
    fixture = TestBed.createComponent(BrandIcon);
    fixture.componentRef.setInput('name', inputs.name);
    if (inputs.label !== undefined) {
      fixture.componentRef.setInput('label', inputs.label);
    }
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector('svg') as SVGElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('references the sprite symbol for the named mark', async () => {
    const svg = await render({ name: 'brand-microsoft' });

    expect(svg.querySelector('use')?.getAttribute('href')).toBe('#brand-microsoft');
  });

  it('is decoration until a label promotes it to content', async () => {
    const decorative = await render({ name: 'brand-context7' });
    expect(decorative.getAttribute('aria-hidden')).toBe('true');
    expect(decorative.getAttribute('role')).toBeNull();

    const labelled = await render({ name: 'brand-context7', label: 'Context7' });
    expect(labelled.getAttribute('aria-hidden')).toBeNull();
    expect(labelled.getAttribute('role')).toBe('img');
    expect(labelled.getAttribute('aria-label')).toBe('Context7');
  });

  // The fill invariant that separates this from `app-icon` is asserted where it is
  // falsifiable — `icon-names.spec.ts` reads the emitted sprite and requires
  // `fill="currentColor"` on the `bi-` lane and on nothing else. Asserting it here
  // through `getComputedStyle` cannot fail: jsdom normalises the keyword to
  // lowercase, so every spelling of the check passes whatever the stylesheet says.
});
