import { ChangeDetectionStrategy, Component, TemplateRef, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ShellChrome } from '@core/ui/shell-chrome';
import { beforeEach, describe, expect, it } from 'vitest';
import { MobileNavbar } from './mobile-navbar';

/**
 * The join between the two halves of the navbar slot.
 *
 * `chat.spec.ts` proves a screen hands a `TemplateRef` over, and `shell-responsive.spec.ts`
 * proves the shell renders a navbar — but nothing proved the navbar renders what was
 * handed to it. A regression there would leave both of those suites green and the phone
 * with a bar that shows the product name and no controls.
 */
@Component({
  selector: 'app-navbar-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MobileNavbar],
  template: `
    <ng-template #actions><button class="published-action" type="button">Act</button></ng-template>
    <app-mobile-navbar (menuRequested)="opened = opened + 1" />
  `,
})
class NavbarHost {
  readonly actions = viewChild.required<TemplateRef<unknown>>('actions');
  opened = 0;
}

describe('MobileNavbar (US-1403)', () => {
  let chrome: ShellChrome;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [ShellChrome] });
    chrome = TestBed.inject(ShellChrome);
  });

  it('shows the product name until a screen publishes one of its own', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    // Frames 1d and 5m: the brand on chat, the area's name under /admin.
    expect(element.querySelector('.navbar__title')?.textContent?.trim()).toBe('Enterprise GPT');

    chrome.publish({ title: 'Admin' });
    await fixture.whenStable();

    expect(element.querySelector('.navbar__title')?.textContent?.trim()).toBe('Admin');
  });

  it('renders the trailing controls a screen published, and none when it publishes none', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    // Frame 5m draws no trailing control at all, which is what `actions: null` is for.
    expect(element.querySelector('.navbar__actions')).toBeNull();

    chrome.publish({ actions: fixture.componentInstance.actions() });
    await fixture.whenStable();

    expect(element.querySelector('.navbar__actions .published-action')).not.toBeNull();

    chrome.clear();
    await fixture.whenStable();

    expect(element.querySelector('.navbar__actions')).toBeNull();
  });

  it('names the hamburger and says what it opens', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const hamburger = element.querySelector<HTMLButtonElement>('.navbar__menu');

    expect(hamburger?.getAttribute('aria-label')).toBe('Open navigation');
    // `aria-haspopup="dialog"` and deliberately no `aria-expanded`: what opens is a modal
    // dialog in the top layer, which inerts this button rather than revealing a region
    // beside it.
    expect(hamburger?.getAttribute('aria-haspopup')).toBe('dialog');
    expect(hamburger?.hasAttribute('aria-expanded')).toBe(false);

    hamburger?.click();
    await fixture.whenStable();

    expect(fixture.componentInstance.opened).toBe(1);
  });

  it('keeps the brand text out of the heading outline', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();

    // The routed screen below owns the document's only <h1> — on chat that is the
    // conversation name, clipped at this width but still announced. Promoting brand text
    // to a heading would give the page two.
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('h1, h2, h3')).toHaveLength(0);
  });
});
