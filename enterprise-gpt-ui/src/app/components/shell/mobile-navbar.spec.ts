import { ChangeDetectionStrategy, Component, TemplateRef, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { MobileNavbar } from './mobile-navbar';

/** Proves the navbar renders what a screen hands it; the chat and shell specs stop short. */
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

describe('MobileNavbar', () => {
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

    // The brand on chat, the area's name under /admin.
    expect(element.querySelector('.navbar__title')?.textContent?.trim()).toBe('Enterprise GPT');

    chrome.publish({ title: 'Admin' });
    await fixture.whenStable();

    expect(element.querySelector('.navbar__title')?.textContent?.trim()).toBe('Admin');
  });

  it('renders the trailing controls a screen published, and none when it publishes none', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    // A screen with no trailing control is what `actions: null` is for.
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
    // No `aria-expanded`: a modal dialog inerts this button rather than revealing a region.
    expect(hamburger?.getAttribute('aria-haspopup')).toBe('dialog');
    expect(hamburger?.hasAttribute('aria-expanded')).toBe(false);

    hamburger?.click();
    await fixture.whenStable();

    expect(fixture.componentInstance.opened).toBe(1);
  });

  it('keeps the brand text out of the heading outline', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();

    // The routed screen owns the only <h1>; brand text as a heading would add a second.
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('h1, h2, h3')).toHaveLength(0);
  });
});
