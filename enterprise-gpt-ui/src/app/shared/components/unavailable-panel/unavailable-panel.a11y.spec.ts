import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { UnavailablePanel } from './unavailable-panel';

/**
 * The harness canary: passing means Chromium, the app stylesheet, theme tokens and axe all work,
 * so check it first when another `*.a11y.spec.ts` fails.
 */
describe('UnavailablePanel accessibility', () => {
  let fixture: ComponentFixture<UnavailablePanel> | null = null;

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    clearTheme();
  });

  it('runs against computed style rather than jsdom stubs', () => {
    // Fails in jsdom, which has no cascade, so `--surface` would come back empty.
    const read = () =>
      getComputedStyle(document.documentElement).getPropertyValue('--surface').trim();

    applyTheme('light');
    const light = read();
    applyTheme('dark');
    const dark = read();

    expect(light).not.toBe('');
    // Were the themes equal, every dark-theme assertion would silently measure light.
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
      // Attached: axe reads computed style, which a detached element does not have.
      document.body.append(fixture.nativeElement as HTMLElement);
      await fixture.whenStable();

      await expectNoSeriousViolations(
        fixture.nativeElement as Element,
        `UnavailablePanel (${theme})`,
      );
    });
  }
});
