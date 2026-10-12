import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AppError } from '@core/errors/app-error';
import { ErrorPanel } from './error-panel';

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
