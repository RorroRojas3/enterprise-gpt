import { TestBed } from '@angular/core/testing';
import { PREFERENCE_KEYS } from '@core/storage/local-preferences';
import { PREFERS_DARK, resetMediaQueries, setMediaQuery } from '@testing/media-query';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ThemeToggle } from './theme-toggle';

describe('ThemeToggle', () => {
  async function render() {
    const fixture = TestBed.createComponent(ThemeToggle);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      button: host.querySelector('button') as HTMLButtonElement,
      icons: [...host.querySelectorAll('app-icon')],
    };
  }

  beforeEach(() => {
    localStorage.clear();
    resetMediaQueries();
    setMediaQuery(PREFERS_DARK, false);
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    document.documentElement.removeAttribute('data-bs-theme');
    document.documentElement.style.removeProperty('color-scheme');
  });

  it('is a real button with an accessible name', async () => {
    const { button } = await render();

    expect(button.type).toBe('button');
    expect(button.getAttribute('aria-label')).toBe('Switch to dark theme');
  });

  it('flips the theme and records it', async () => {
    const { button, fixture } = await render();

    button.click();
    await fixture.whenStable();

    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('dark');
    expect(localStorage.getItem(PREFERENCE_KEYS.theme)).toBe('dark');
    expect(button.getAttribute('aria-label')).toBe('Switch to light theme');
  });

  it('keeps both icons in the DOM, proving the swap is CSS and not @if', async () => {
    const { icons, button, fixture } = await render();

    expect(icons).toHaveLength(2);

    button.click();
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelectorAll('app-icon')).toHaveLength(2);
  });

  it('does not announce a pressed state — the label already carries it', async () => {
    const { button } = await render();

    expect(button.getAttribute('aria-pressed')).toBeNull();
  });
});
