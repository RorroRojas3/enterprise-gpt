import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { settle } from '@testing/async';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { provideFakeNavigation } from '@testing/navigation';
import { userFixture } from '@testing/session';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthService } from '../auth-service';
import { sessionGuard } from './session.guard';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;
const CONVERSATIONS_URL = `${TEST_API_BASE_URL}/api/conversations/search`;
const PROJECTS_URL = `${TEST_API_BASE_URL}/api/projects`;

describe('sessionGuard', () => {
  let backend: HttpTestingController;

  function run(): Promise<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      sessionGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
    ) as Promise<boolean | UrlTree>;
  }

  /** The four loads the bootstrap starts once the session has resolved. */
  function flushPostBootstrapLoads(): void {
    backend.expectOne(MODELS_URL).flush([]);
    backend.expectOne(MCPS_URL).flush([]);
    backend
      .expectOne((request) => request.url === CONVERSATIONS_URL)
      .flush({ items: [], totalCount: 0, pageSize: 50, currentPage: 1, totalPages: 0 });
    // US-307's root project list, the last of US-202's four post-bootstrap loads. One
    // page with nothing beyond it, so the drain stops here.
    backend
      .expectOne((request) => request.url === PROJECTS_URL)
      .flush({ items: [], totalCount: 0, pageSize: 100, currentPage: 1, totalPages: 0 });
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // The guard consults AuthService.isSigningOut before it does anything else,
        // and AuthService reaches MSAL on construction.
        provideFakeMsal(signedInMsal({ logoutRedirect: () => new Promise<void>(() => undefined) })),
        provideFakeNavigation(),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  it('activates once the session resolves', async () => {
    const activated = run();
    await settle();
    backend.expectOne({ method: 'POST', url: ME_URL }).flush(userFixture());
    await settle();

    flushPostBootstrapLoads();

    await expect(activated).resolves.toBe(true);
  });

  it('requests the catalogs only after the bootstrap has returned', async () => {
    // US-202: the bootstrap self-provisions the user and warms the API's permission
    // cache, so a catalog request racing it could be answered before either happened.
    const activated = run();
    await settle();

    backend.expectNone(MODELS_URL);
    backend.expectNone(MCPS_URL);
    backend.expectNone((request) => request.url === CONVERSATIONS_URL);
    backend.expectNone((request) => request.url === PROJECTS_URL);

    backend.expectOne(ME_URL).flush(userFixture());
    await settle();

    flushPostBootstrapLoads();
    await activated;
  });

  it('sends a failed bootstrap to frame 6c rather than cancelling the navigation', async () => {
    // A bare false would fall through to the wildcard, which redirects here again.
    const activated = run();
    await settle();
    backend
      .expectOne(ME_URL)
      .flush(PROBLEM_FIXTURES.resourceNotFound, { status: 404, statusText: 'Not Found' });

    const result = await activated;

    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/session-error');
  });

  it('does not re-issue the bootstrap on a second navigation', async () => {
    const first = run();
    await settle();
    backend.expectOne(ME_URL).flush(userFixture());
    await settle();
    flushPostBootstrapLoads();
    await first;

    await expect(run()).resolves.toBe(true);
    await settle();

    backend.verify();
  });

  it('does not repopulate the store for the user who just signed out', async () => {
    // SessionStore drops its bootstrap memo on sign-out, so an unlatched run here would
    // issue a fresh POST api/users/me and refill the store this teardown just emptied.
    const first = run();
    await settle();
    backend.expectOne(ME_URL).flush(userFixture());
    await settle();
    flushPostBootstrapLoads();
    await first;

    void TestBed.inject(AuthService).signOut();

    await expect(run()).resolves.toBe(false);
    await settle();

    backend.verify();
  });
});
