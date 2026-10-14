import { ChangeDetectionStrategy, Component, TemplateRef, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryAllByRole, queryByRole } from '@testing-library/dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { MobileNavbar } from './mobile-navbar';

/** Proves the navbar renders what a screen hands it; the chat and shell specs stop short. */
@Component({
  selector: 'app-navbar-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MobileNavbar],
  template: `
    <ng-template #actions><button type="button">Act</button></ng-template>
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
    expect(getByText(element, 'Enterprise GPT')).toBeTruthy();

    chrome.publish({ title: 'Admin' });
    await fixture.whenStable();

    expect(getByText(element, 'Admin')).toBeTruthy();
    expect(element.textContent).not.toContain('Enterprise GPT');
  });

  it('renders the trailing controls a screen published, and none when it publishes none', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    // A screen with no trailing control is what `actions: null` is for.
    expect(queryByRole(element, 'button', { name: 'Act' })).toBeNull();

    chrome.publish({ actions: fixture.componentInstance.actions() });
    await fixture.whenStable();

    expect(getByRole(element, 'button', { name: 'Act' })).toBeTruthy();

    chrome.clear();
    await fixture.whenStable();

    expect(queryByRole(element, 'button', { name: 'Act' })).toBeNull();
  });

  it('names the hamburger and says what it opens', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const hamburger = getByRole(element, 'button', { name: 'Open navigation' });

    // No `aria-expanded`: a modal dialog inerts this button rather than revealing a region.
    expect(hamburger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(hamburger.hasAttribute('aria-expanded')).toBe(false);

    hamburger.click();
    await fixture.whenStable();

    expect(fixture.componentInstance.opened).toBe(1);
  });

  it('keeps the brand text out of the heading outline', async () => {
    const fixture = TestBed.createComponent(NavbarHost);
    await fixture.whenStable();

    // The routed screen owns the only <h1>; brand text as a heading would add a second.
    expect(queryAllByRole(fixture.nativeElement as HTMLElement, 'heading')).toHaveLength(0);
  });
});
