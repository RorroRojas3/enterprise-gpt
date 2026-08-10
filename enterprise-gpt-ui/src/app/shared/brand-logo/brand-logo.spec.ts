import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BrandLogo, BrandVariant } from './brand-logo';

describe('BrandLogo', () => {
  async function render(inputs: { variant?: BrandVariant; alt?: string } = {}) {
    const fixture = TestBed.createComponent(BrandLogo);
    if (inputs.variant) {
      fixture.componentRef.setInput('variant', inputs.variant);
    }
    if (inputs.alt !== undefined) {
      fixture.componentRef.setInput('alt', inputs.alt);
    }
    await fixture.whenStable();
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('img')];
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('renders both theme variants so the swap needs no script', async () => {
    const images = await render();

    expect(images).toHaveLength(2);
    expect(images[0]?.getAttribute('src')).toBe('brand/logo-full.webp');
    expect(images[1]?.getAttribute('src')).toBe('brand/logo-full-dark.webp');
    expect(images[0]?.className).toContain('brand--light');
    expect(images[1]?.className).toContain('brand--dark');
  });

  it('gives both images the same alt, so neither theme reads the wordmark twice', async () => {
    const images = await render({ alt: 'Andes Software Solutions' });

    expect(images.map((image) => image.getAttribute('alt'))).toEqual([
      'Andes Software Solutions',
      'Andes Software Solutions',
    ]);
  });

  it('is decorative by default, for the places it sits beside its own name', async () => {
    const images = await render();

    expect(images.map((image) => image.getAttribute('alt'))).toEqual(['', '']);
  });

  it('swaps both sources and the reserved size for the mark', async () => {
    const images = await render({ variant: 'mark' });

    expect(images.map((image) => image.getAttribute('src'))).toEqual([
      'brand/logo-mark.webp',
      'brand/logo-mark-dark.webp',
    ]);
    expect(images[0]?.getAttribute('width')).toBe('128');
    expect(images[0]?.getAttribute('height')).toBe('51');
  });
});
