import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { sessionEvents } from '@core/events/session-events';
import { Dispatcher } from '@ngrx/signals/events';
import { PERMISSION_ID } from '@domain/api/permission-ids';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { MCP_GRANT, administratorFixture, userFixture } from '@testing/session';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { SessionStore } from './session-store';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;

describe('SessionStore', () => {
  let store: InstanceType<typeof SessionStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    store = TestBed.inject(SessionStore);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
  });

  function signOut(): void {
    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());
  }

  it('holds the user and their grants after the bootstrap returns', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne({ method: 'POST', url: ME_URL }).flush(userFixture());

    await expect(loaded).resolves.toBe(true);
    expect(store.isLoaded()).toBe(true);
    expect(store.isFulfilled()).toBe(true);
    expect(store.displayName()).toBe('Ada Lovelace');
    expect(store.canUploadFiles()).toBe(true);
  });

  it('treats a user with no grants at all as a valid session, not a failure', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(userFixture({ permissions: [] }));

    await expect(loaded).resolves.toBe(true);
    expect(store.isFulfilled()).toBe(true);
    expect(store.error()).toBeNull();
    expect(store.permissionIds().size).toBe(0);
    // Loaded-with-nothing must stay distinguishable from never-loaded.
    expect(store.isLoaded()).toBe(true);
  });

  it('issues the bootstrap once however many navigations ask for it', async () => {
    // canActivate re-runs on every navigation; an unmemoized load would re-POST
    // users/me on each one.
    const first = store.ensureLoaded();
    const second = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(userFixture());

    await Promise.all([first, second]);
    await store.ensureLoaded();

    backend.verify();
  });

  it('recognises the Administrator grant by id', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(administratorFixture());
    await loaded;

    expect(store.isAdministrator()).toBe(true);
  });

  it('does not infer Upload File from Administrator', async () => {
    // US-204: an administrator who does not hold the grant may not upload.
    const loaded = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(
      userFixture({
        permissions: [
          {
            id: PERMISSION_ID.administrator,
            name: 'Administrator',
            description: null,
            isDefault: false,
            mcpServerId: null,
          },
        ],
      }),
    );
    await loaded;

    expect(store.isAdministrator()).toBe(true);
    expect(store.canUploadFiles()).toBe(false);
  });

  it('matches an MCP-derived grant by id rather than by name', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(userFixture({ permissions: [MCP_GRANT] }));
    await loaded;

    expect(store.hasPermission(MCP_GRANT.id)).toBe(true);
    expect(store.hasPermission('00000000-0000-0000-0000-000000000000')).toBe(false);
  });

  it('reports a deactivated account as a typed failure', async () => {
    const loaded = store.ensureLoaded();
    backend
      .expectOne(ME_URL)
      .flush(PROBLEM_FIXTURES.forbidden, { status: 403, statusText: 'Forbidden' });

    await expect(loaded).resolves.toBe(false);
    expect(store.error()?.kind).toBe('forbidden');
    expect(store.isLoaded()).toBe(false);
  });

  it('re-issues the bootstrap on reload, which is frame 6c’s Retry', async () => {
    const failed = store.ensureLoaded();
    backend.expectOne(ME_URL).flush('', { status: 503, statusText: 'Service Unavailable' });
    await failed;

    const retried = store.reload();
    backend.expectOne(ME_URL).flush(userFixture());

    await expect(retried).resolves.toBe(true);
    expect(store.error()).toBeNull();
  });

  it('abandons a bootstrap that sign-out overtook', async () => {
    // Clearing state is not the same as cancelling work. Without the takeUntil the
    // request would still resolve and write the previous user back over the state
    // that had just been emptied.
    const loaded = store.ensureLoaded();
    const request = backend.expectOne(ME_URL);

    signOut();

    expect(request.cancelled).toBe(true);
    await expect(loaded).resolves.toBe(false);
    expect(store.user()).toBeNull();
    // Idle, not errored and not still pending. Reporting the user's own sign-out as a
    // failure would send sessionGuard to frame 6c, which then finds no error to explain
    // and bounces straight back — and `pending` is a state no screen renders at all.
    expect(store.isIdle()).toBe(true);
    expect(store.error()).toBeNull();
  });

  it('lets a reload win over an earlier load that is still in flight', async () => {
    // rxMethod + switchMap is the usual guarantee; a guard has to await this instead,
    // so a generation counter carries it. Without one the slow first response lands
    // last and paints stale data over the fresh result.
    const first = store.ensureLoaded();
    const slow = backend.expectOne(ME_URL);

    const second = store.reload();
    const fast = backend.expectOne(ME_URL);

    fast.flush(administratorFixture());
    await second;

    slow.flush(userFixture({ permissions: [] }));
    await first;

    expect(store.isAdministrator()).toBe(true);
    expect(store.isFulfilled()).toBe(true);
  });

  it('clears the bootstrap memo on sign-out, so the next user is fetched afresh', async () => {
    // The memo is a prop rather than state, so withResetOnSignOut does not reach it.
    // Left alone, the next user on a shared machine inherits this resolved promise
    // and users/me is never called for them.
    const loaded = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(userFixture());
    await loaded;

    signOut();
    expect(store.user()).toBeNull();

    const second = store.ensureLoaded();
    backend.expectOne(ME_URL).flush(administratorFixture());
    await second;

    expect(store.isAdministrator()).toBe(true);
  });
});
