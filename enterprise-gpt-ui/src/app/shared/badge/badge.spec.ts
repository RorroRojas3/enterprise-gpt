import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ProjectCard } from '../card/project-card/project-card';
import { KindBadge } from './kind-badge/kind-badge';
import { PermissionBadge } from './permission-badge/permission-badge';
import { SourceBadge } from './source-badge/source-badge';
import { StatusDot } from './status-dot/status-dot';

describe('StatusDot', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(inputs: { label: string; tone?: string; labelHidden?: boolean }) {
    const fixture = TestBed.createComponent(StatusDot);
    fixture.componentRef.setInput('label', inputs.label);
    if (inputs.tone) fixture.componentRef.setInput('tone', inputs.tone);
    if (inputs.labelHidden !== undefined) {
      fixture.componentRef.setInput('labelHidden', inputs.labelHidden);
    }
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('keeps the meaning in text, so colour is never the only channel', async () => {
    const host = await render({ label: 'Active', tone: 'ok' });

    expect(host.textContent).toContain('Active');
    expect(host.querySelector('.status-dot__mark')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('hides the label visually without removing it from the accessibility tree', async () => {
    const host = await render({ label: 'Active', labelHidden: true });

    const label = host.querySelector('.visually-hidden');
    expect(label?.textContent).toContain('Active');
  });

  it.each(['provider-azure-openai', 'provider-bedrock', 'provider-anthropic'])(
    'renders the %s provider tone the model picker uses',
    async (tone) => {
      const host = await render({ label: '', tone, labelHidden: true });

      expect(host.querySelector(`.status-dot__mark--${tone}`)).not.toBeNull();
    },
  );
});

describe('KindBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(kind: string) {
    const fixture = TestBed.createComponent(KindBadge);
    fixture.componentRef.setInput('kind', kind);
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).textContent?.trim();
  }

  it.each([
    ['McpTool', 'MCP tool'],
    ['Agent', 'Agent'],
    ['Function', 'Function'],
    // US-501: exactly four badge values; Unknown is the reasoning arm.
    ['Unknown', 'Reasoning'],
  ])('renders %s as %s', async (kind, label) => {
    expect(await render(kind)).toBe(label);
  });

  it('renders a kind it has never seen rather than nothing', async () => {
    expect(await render('somethingNew')).toBe('SomethingNew');
  });
});

describe('PermissionBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('spells out why a grant is locked, rather than relying on the padlock', async () => {
    const fixture = TestBed.createComponent(PermissionBadge);
    fixture.componentRef.setInput('name', 'Upload File');
    fixture.componentRef.setInput('managedBy', 'Jira Cloud MCP');
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.visually-hidden')?.textContent).toContain('Jira Cloud MCP');
    expect(host.querySelector('app-icon')).not.toBeNull();
  });

  it('shows no padlock for a grant an administrator can revoke', async () => {
    const fixture = TestBed.createComponent(PermissionBadge);
    fixture.componentRef.setInput('name', 'Upload File');
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('app-icon')).toBeNull();
  });
});

describe('SourceBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it.each([
    ['uploaded', 'Uploaded'],
    ['generated', 'Generated'],
  ])('says %s in words, not only in colour', async (source, label) => {
    const fixture = TestBed.createComponent(SourceBadge);
    fixture.componentRef.setInput('source', source);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe(label);
  });
});

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
    // The label alone carries the state — no aria-pressed, so it is not announced twice.
    expect(button?.getAttribute('aria-pressed')).toBeNull();
    expect(button?.getAttribute('aria-label')).toBe('Unfavourite Helios 2.4');
    expect(button?.classList.contains('project-card__favorite--on')).toBe(true);

    button?.click();
    expect(toggled).toHaveBeenCalledOnce();
  });

  it('marks only itself busy while its own action is in flight', async () => {
    const { host } = await render({ pending: true });

    expect(host.querySelector('article')?.getAttribute('aria-busy')).toBe('true');
    // `aria-disabled`, never the native attribute: the store sets the pending id
    // synchronously with the click, so a native `disabled` would blur the very button
    // the reader just pressed and drop focus to <body> for the whole round trip.
    // `ProjectActionsStore.toggleFavorite` guards on the pending id, so the press that
    // still reaches the handler is a real no-op.
    const star = host.querySelector<HTMLButtonElement>('.project-card__favorite');
    expect(star?.getAttribute('aria-disabled')).toBe('true');
    expect(star?.disabled).toBe(false);
  });
});
