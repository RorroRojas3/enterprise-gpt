import { TestBed } from '@angular/core/testing';
import { getByRole } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ToastRegion } from './toast-region';
import { ToastStore } from './toast-store';

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

    expect(getByRole(host, 'button', { name: 'Dismiss: Upload failed' })).toBeTruthy();
  });

  it('renders a draining indicator only on a toast that dismisses itself', async () => {
    const { fixture, host, store } = await render();

    store.success('Saved');
    store.show({ tone: 'error', title: 'Failed' });
    await fixture.whenStable();

    expect(host.querySelector('[role="status"] .toast__drain')).not.toBeNull();
    expect(host.querySelector('[role="alert"] .toast__drain')).toBeNull();
  });

  it('removes a toast from the DOM when its dismiss control is used', async () => {
    const { fixture, host, store } = await render();
    store.show({ tone: 'error', title: 'Upload failed' });
    await fixture.whenStable();

    getByRole(host, 'button', { name: 'Dismiss: Upload failed' }).click();
    await fixture.whenStable();

    expect(host.querySelector('app-toast-item')).toBeNull();
  });

  describe('focus', () => {
    let main: HTMLElement;
    let attached: HTMLElement;

    beforeEach(() => {
      main = document.createElement('main');
      main.id = 'main-content';
      main.tabIndex = -1;
      document.body.append(main);
    });

    afterEach(() => {
      main.remove();
      attached.remove();
    });

    async function renderAttached() {
      const rendered = await render();
      attached = rendered.host;
      document.body.append(attached);

      return rendered;
    }

    it('moves to the main content when the focused dismiss control leaves', async () => {
      const { fixture, host, store } = await renderAttached();
      store.show({ tone: 'error', title: 'Upload failed' });
      await fixture.whenStable();

      const dismiss = getByRole(host, 'button', { name: 'Dismiss: Upload failed' });
      dismiss.focus();
      dismiss.click();
      await fixture.whenStable();

      expect(document.activeElement).toBe(main);
    });

    it('moves to the main content when the focused action leaves', async () => {
      const { fixture, host, store } = await renderAttached();
      store.show({
        tone: 'info',
        title: 'Answer ready',
        action: { label: 'Open', run: () => undefined },
      });
      await fixture.whenStable();

      const action = getByRole(host, 'button', { name: 'Open' });
      action.focus();
      action.click();
      await fixture.whenStable();

      expect(document.activeElement).toBe(main);
    });

    it('moves to the main content when a focused toast is replaced by its key', async () => {
      const { fixture, host, store } = await renderAttached();
      store.show({ tone: 'error', title: 'Upload failed', key: 'upload' });
      await fixture.whenStable();
      getByRole(host, 'button', { name: 'Dismiss: Upload failed' }).focus();

      store.show({ tone: 'error', title: 'Upload failed again', key: 'upload' });
      await fixture.whenStable();

      expect(document.activeElement).toBe(main);
    });

    it('leaves focus on a toast that stays while another one goes', async () => {
      const { fixture, host, store } = await renderAttached();
      store.show({ tone: 'error', title: 'First' });
      const second = store.show({ tone: 'error', title: 'Second' });
      await fixture.whenStable();
      const stays = getByRole(host, 'button', { name: 'Dismiss: First' });
      stays.focus();

      store.dismiss(second);
      await fixture.whenStable();

      expect(document.activeElement).toBe(stays);
    });

    it('leaves focus alone when the reader is elsewhere', async () => {
      const { fixture, host, store } = await renderAttached();
      store.show({ tone: 'error', title: 'Upload failed' });
      await fixture.whenStable();
      const elsewhere = main.appendChild(document.createElement('button'));
      elsewhere.focus();

      getByRole(host, 'button', { name: 'Dismiss: Upload failed' }).click();
      await fixture.whenStable();

      expect(document.activeElement).toBe(elsewhere);
    });
  });
});
