import { TestBed } from '@angular/core/testing';
import { AppError } from '@core/errors/app-error';
import { ToastStore } from '@core/notifications/toast-store';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EmptyState } from './empty-state/empty-state';
import { ErrorPanel } from './error-panel/error-panel';
import { Skeleton } from './skeleton/skeleton';
import { ToastRegion } from './toast/toast-region';
import { UnavailablePanel } from './unavailable-panel/unavailable-panel';

const NOT_FOUND = {
  kind: 'resource-not-found',
  status: 404,
  title: 'Not Found',
  detail: null,
  traceId: '8f42-a1c9-77d0-3b61',
  instance: null,
} as AppError;

describe('ErrorPanel', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render() {
    const fixture = TestBed.createComponent(ErrorPanel);
    fixture.componentRef.setInput('error', NOT_FOUND);
    fixture.componentRef.setInput('heading', "Documents couldn't load");
    await fixture.whenStable();
    return fixture;
  }

  it('says what failed, why, and which trace id to quote', async () => {
    const host = (await render()).nativeElement as HTMLElement;

    expect(host.textContent).toContain("Documents couldn't load");
    expect(host.textContent).toContain('That item no longer exists');
    expect(host.textContent).toContain('Trace ID: 8f42-a1c9-77d0-3b61');
  });

  it('announces itself, because it usually replaces a loading skeleton', async () => {
    const host = (await render()).nativeElement as HTMLElement;

    expect(host.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('offers a retry, which is what separates it from the unavailable panel', async () => {
    const fixture = await render();
    const retried = vi.fn();
    fixture.componentInstance.retried.subscribe(retried);

    (fixture.nativeElement as HTMLElement).querySelector('button')?.click();

    expect(retried).toHaveBeenCalledOnce();
  });
});

describe('UnavailablePanel', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render() {
    const fixture = TestBed.createComponent(UnavailablePanel);
    fixture.componentRef.setInput('heading', "Your documents aren't available yet");
    fixture.componentRef.setInput(
      'message',
      'The documents API is not enabled for this deployment.',
    );
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('names the missing capability', async () => {
    const host = await render();

    expect(host.textContent).toContain("Your documents aren't available yet");
    expect(host.textContent).toContain('not enabled for this deployment');
  });

  it('offers nothing to press — a missing API does not come back on retry', async () => {
    const host = await render();

    expect(host.querySelector('button')).toBeNull();
    expect(host.querySelector('a')).toBeNull();
  });

  it('is not a live region: it is the steady state of the screen, not news', async () => {
    const host = await render();

    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(host.querySelector('[aria-live]')).toBeNull();
  });

  it('renders no rows, records or placeholder values', async () => {
    const host = await render();

    expect(host.querySelectorAll('table, tr, li')).toHaveLength(0);
  });
});

describe('EmptyState', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('exposes a heading at the level the caller asked for', async () => {
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('heading', 'No conversations match "quarterly"');
    fixture.componentRef.setInput('headingLevel', 2);
    await fixture.whenStable();

    const heading = (fixture.nativeElement as HTMLElement).querySelector('[role="heading"]');
    expect(heading?.getAttribute('aria-level')).toBe('2');
    expect(heading?.textContent).toContain('No conversations match');
  });

  it('can drop the illustration for a compact surface such as the sidebar', async () => {
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('heading', 'Nothing here yet');
    fixture.componentRef.setInput('illustration', 'none');
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('app-ridgeline')).toBeNull();
  });
});

describe('Skeleton', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  it('is hidden from assistive technology', async () => {
    const fixture = TestBed.createComponent(Skeleton);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).getAttribute('aria-hidden')).toBe('true');
  });

  it('renders one bar per line, with the last one short', async () => {
    const fixture = TestBed.createComponent(Skeleton);
    fixture.componentRef.setInput('lines', 3);
    await fixture.whenStable();

    const bars = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.skeleton')];
    expect(bars).toHaveLength(3);
    expect((bars[2] as HTMLElement).style.width).toBe('60%');
  });
});

describe('ToastRegion', () => {
  // Real timers here, unlike toast-store.spec.ts: the zoneless scheduler settles
  // through setTimeout, so faking it makes `fixture.whenStable()` never resolve. This
  // suite asserts structure rather than timing; the timing lives in the store's spec.
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    // Auto-dismiss timers are real in this suite, so clear them rather than let one
    // fire against a destroyed fixture.
    TestBed.inject(ToastStore).dismissAll();
  });

  async function render() {
    const fixture = TestBed.createComponent(ToastRegion);
    await fixture.whenStable();
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      store: TestBed.inject(ToastStore),
    };
  }

  it('has both live regions in the DOM before any toast exists', async () => {
    const { host } = await render();

    expect(host.querySelector('[role="status"][aria-live="polite"]')).not.toBeNull();
    expect(host.querySelector('[role="alert"][aria-live="assertive"]')).not.toBeNull();
  });

  it('routes each tone to the region that matches its urgency', async () => {
    const { fixture, host, store } = await render();

    store.success('Saved');
    store.show({ tone: 'error', title: 'Upload failed' });
    await fixture.whenStable();

    expect(host.querySelector('[role="status"]')?.textContent).toContain('Saved');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Upload failed');
  });

  it('gives the dismiss control an accessible name that names the toast', async () => {
    const { fixture, host, store } = await render();
    store.show({ tone: 'error', title: 'Upload failed' });
    await fixture.whenStable();

    const dismiss = host.querySelector('.toast__dismiss');
    expect(dismiss?.getAttribute('aria-label')).toBe('Dismiss: Upload failed');
  });

  it('renders a draining indicator only for toasts that dismiss themselves', async () => {
    const { fixture, host, store } = await render();

    store.success('Saved');
    store.show({ tone: 'error', title: 'Failed' });
    await fixture.whenStable();

    const drains = host.querySelectorAll('.toast__drain');
    expect(drains).toHaveLength(1);
    expect(host.querySelector('[role="status"] .toast__drain')).not.toBeNull();
  });

  it('removes a toast from the DOM when its dismiss control is used', async () => {
    const { fixture, host, store } = await render();
    store.show({ tone: 'error', title: 'Upload failed' });
    await fixture.whenStable();

    host.querySelector<HTMLButtonElement>('.toast__dismiss')?.click();
    await fixture.whenStable();

    expect(host.querySelector('app-toast-item')).toBeNull();
  });
});
