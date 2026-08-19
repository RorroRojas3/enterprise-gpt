import { ComponentFixture, TestBed } from '@angular/core/testing';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { afterEach, describe, expect, it } from 'vitest';
import { UnavailablePanel } from './unavailable-panel';

/**
 * The smallest real component, checked in both themes — and the proof that the browser
 * harness itself works.
 *
 * If this file passes, then Chromium launched, the application's stylesheet was loaded
 * through the target's `buildTarget`, `data-bs-theme` resolved the token palette, and
 * axe ran against genuinely computed style. Every other `*.a11y.spec.ts` rests on that,
 * and when one of them fails it is worth knowing this one still passes before looking
 * anywhere else.
 */
describe('UnavailablePanel accessibility (US-1405)', () => {
  let fixture: ComponentFixture<UnavailablePanel> | null = null;

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    clearTheme();
  });

  it('runs against computed style rather than jsdom stubs', () => {
    // The one assertion that would fail in jsdom, and the reason this target exists: no
    // cascade means no resolved custom property, so `--surface` would come back empty.
    const read = () =>
      getComputedStyle(document.documentElement).getPropertyValue('--surface').trim();

    applyTheme('light');
    const light = read();
    applyTheme('dark');
    const dark = read();

    expect(light).not.toBe('');
    // And that the two themes are genuinely two: `data-bs-theme` swaps the token palette,
    // which is the whole of what "in both light and dark themes" means for a contrast
    // audit. If these were ever equal, every dark-theme assertion in the suite would be
    // measuring the light one and passing.
    expect(dark).not.toBe(light);
  });

  for (const theme of THEMES) {
    it(`has no serious or critical violations in the ${theme} theme`, async () => {
      applyTheme(theme);

      fixture = TestBed.createComponent(UnavailablePanel);
      fixture.componentRef.setInput('heading', 'Usage reports aren’t available yet');
      fixture.componentRef.setInput(
        'message',
        'The reporting API is not enabled for this deployment.',
      );
      // Attached, because axe reads computed style and an element outside the document
      // has none.
      document.body.append(fixture.nativeElement as HTMLElement);
      await fixture.whenStable();

      await expectNoSeriousViolations(
        fixture.nativeElement as Element,
        `UnavailablePanel (${theme})`,
      );
    });
  }
});
