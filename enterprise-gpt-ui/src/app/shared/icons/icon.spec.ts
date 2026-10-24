import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Icon } from './icon';

describe('Icon', () => {
  let fixture: ComponentFixture<Icon>;

  async function render(inputs: { name: 'bi-x-lg' | 'bi-trash3'; label?: string }) {
    fixture = TestBed.createComponent(Icon);
    fixture.componentRef.setInput('name', inputs.name);
    if (inputs.label !== undefined) {
      fixture.componentRef.setInput('label', inputs.label);
    }
    await fixture.whenStable();
    return fixture.nativeElement.querySelector('svg') as SVGElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('points <use> at the sprite symbol for its name', async () => {
    const svg = await render({ name: 'bi-x-lg' });

    expect(svg.querySelector('use')?.getAttribute('href')).toBe('#bi-x-lg');
  });

  it('is decorative when no label is supplied', async () => {
    const svg = await render({ name: 'bi-x-lg' });

    expect(svg.getAttribute('aria-hidden')).toBe('true');
    expect(svg.getAttribute('role')).toBeNull();
    expect(svg.getAttribute('aria-label')).toBeNull();
  });

  it('becomes content with an accessible name when labelled', async () => {
    const svg = await render({ name: 'bi-trash3', label: 'Delete conversation' });

    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.getAttribute('aria-label')).toBe('Delete conversation');
    expect(svg.getAttribute('aria-hidden')).toBeNull();
  });

  it('is never a tab stop, which SVG in IE-era markup otherwise is', async () => {
    const svg = await render({ name: 'bi-x-lg' });

    expect(svg.getAttribute('focusable')).toBe('false');
  });
});
