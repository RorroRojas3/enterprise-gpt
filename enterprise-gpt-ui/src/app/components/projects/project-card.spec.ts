import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
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

    const link = host.querySelector('a');
    expect(link?.textContent).toContain('Helios 2.4');
    expect(link?.getAttribute('href')).toBe('/projects/helios');
  });

  it('labels its own heading for assistive technology', async () => {
    const { host } = await render();

    const labelledBy = host.querySelector('article')?.getAttribute('aria-labelledby');
    expect(host.querySelector(`#${labelledBy}`)?.textContent).toContain('Helios 2.4');
  });

  it('names the favourite toggle by what it will do, without double-encoding state', async () => {
    const { fixture, host } = await render({ favorite: true });
    const toggled = vi.fn();
    fixture.componentInstance.favoriteToggled.subscribe(toggled);

    const button = host.querySelector<HTMLButtonElement>('.project-card__favorite');
    expect(button?.getAttribute('aria-pressed')).toBeNull();
    expect(button?.getAttribute('aria-label')).toBe('Unfavourite Helios 2.4');
    expect(button?.classList.contains('project-card__favorite--on')).toBe(true);

    button?.click();
    expect(toggled).toHaveBeenCalledOnce();
  });

  it('marks only itself busy while its own action is in flight', async () => {
    const { host } = await render({ pending: true });

    expect(host.querySelector('article')?.getAttribute('aria-busy')).toBe('true');
    // aria-disabled, not `disabled`, which would blur the button just pressed; the store guards
    // on the pending id, so the press is a no-op.
    const star = host.querySelector<HTMLButtonElement>('.project-card__favorite');
    expect(star?.getAttribute('aria-disabled')).toBe('true');
    expect(star?.disabled).toBe(false);
  });
});
