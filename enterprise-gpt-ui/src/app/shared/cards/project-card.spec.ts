import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { getByRole } from '@testing-library/dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { iconOf } from '@testing/overlays';
import { ProjectCard } from './project-card';

describe('ProjectCard', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  async function render(inputs: { favorite?: boolean; pending?: boolean } = {}) {
    const fixture = TestBed.createComponent(ProjectCard);
    fixture.componentRef.setInput('name', 'Helios 2.4');
    fixture.componentRef.setInput('updatedLabel', '2h ago');
    fixture.componentRef.setInput('link', ['/projects', 'helios']);
    if (inputs.favorite !== undefined) fixture.componentRef.setInput('favorite', inputs.favorite);
    if (inputs.pending !== undefined) fixture.componentRef.setInput('pending', inputs.pending);
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('makes the title a real link, not a clickable card', async () => {
    const { host } = await render();

    const link = getByRole(host, 'link', { name: 'Helios 2.4' });
    expect(link.getAttribute('href')).toBe('/projects/helios');
  });

  it('labels its own heading for assistive technology', async () => {
    const { host } = await render();

    expect(getByRole(host, 'article', { name: 'Helios 2.4' })).toBeTruthy();
  });

  it('names the favourite toggle by what it will do, without double-encoding state', async () => {
    const { fixture, host } = await render({ favorite: true });
    const toggled = vi.fn();
    fixture.componentInstance.favoriteToggled.subscribe(toggled);

    const button = getByRole(host, 'button', { name: 'Unfavourite Helios 2.4' });
    expect(button.getAttribute('aria-pressed')).toBeNull();
    // The filled glyph carries the state the label does not double-encode.
    expect(iconOf(button)).toBe('bi-star-fill');

    button.click();
    expect(toggled).toHaveBeenCalledOnce();
  });

  it('marks only itself busy while its own action is in flight', async () => {
    const { host } = await render({ pending: true });

    expect(host.querySelector('article')?.getAttribute('aria-busy')).toBe('true');
    // aria-disabled, not `disabled`, which would blur the button just pressed; the store guards
    // on the pending id, so the press is a no-op.
    const star = getByRole<HTMLButtonElement>(host, 'button', { name: 'Favourite Helios 2.4' });
    expect(star.getAttribute('aria-disabled')).toBe('true');
    expect(star.disabled).toBe(false);
  });
});
