import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideStartupShellHandoff } from './startup-shell-handoff';

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

const SHELL_MARKUP = `
  <div id="app-shell" data-state="starting">
    <h1 tabindex="-1" id="app-shell-heading">Starting Enterprise GPT…</h1>
    <div id="app-shell-status"><code id="app-shell-detail"></code></div>
    <button type="button" id="app-shell-retry">Try again</button>
  </div>
`;

function shell(): HTMLElement | null {
  return document.getElementById('app-shell');
}

describe('provideStartupShellHandoff', () => {
  let container: HTMLElement;

  beforeEach(() => {
    container = document.createElement('div');
    container.innerHTML = SHELL_MARKUP;
    document.body.append(container);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'page', component: Blank },
          { path: 'refused', component: Blank, canActivate: [() => false] },
          {
            path: 'broken',
            loadComponent: () => Promise.reject(new Error('Loading chunk failed')),
          },
        ]),
        provideStartupShellHandoff(),
      ],
    });
  });

  afterEach(() => {
    container.remove();
  });

  it('removes the shell in the render after the first navigation settles', async () => {
    await TestBed.inject(Router).navigateByUrl('/page');
    expect(shell()).not.toBeNull();

    await TestBed.tick();

    expect(shell()).toBeNull();
  });

  it('removes the shell after a navigation to where the router already is', async () => {
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/');
    await TestBed.tick();

    expect(shell()).toBeNull();
  });

  it('turns the shell into the failure screen when the first navigation fails', async () => {
    await expect(TestBed.inject(Router).navigateByUrl('/broken')).rejects.toThrow();
    // Not yet: bootstrap still needs the `<app-root>` the failure screen removes.
    expect(shell()?.dataset['state']).toBe('starting');

    await TestBed.tick();

    expect(shell()?.dataset['state']).toBe('failed');
  });

  it('leaves the shell up through a cancelled navigation, as a redirect follows', async () => {
    await TestBed.inject(Router).navigateByUrl('/refused');
    await TestBed.tick();

    expect(shell()?.dataset['state']).toBe('starting');
  });
});
