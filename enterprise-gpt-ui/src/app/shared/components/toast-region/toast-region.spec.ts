import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ToastStore } from '@core/notifications/toast-store';
import { ToastRegion } from './toast-region';

describe('ToastRegion', () => {
  // Real timers: the zoneless scheduler settles through setTimeout, so faking it stalls
  // `whenStable()`. Timing is covered in toast-store.spec.ts.
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    // Real auto-dismiss timers, so clear them before one fires against a destroyed fixture.
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
