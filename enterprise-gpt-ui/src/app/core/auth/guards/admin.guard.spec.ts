import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { IPublicClientApplication } from '@azure/msal-browser';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { provideFakeMsal, signedInMsal, signedOutMsal } from '@testing/msal';
import { administratorFixture, userFixture } from '@testing/session';
import { UserDto } from '@domain/api/user';
import { Mock, beforeEach, describe, expect, it, vi } from 'vitest';
import { adminCanMatch } from './admin.guard';

const ME_URL = `${TEST_API_BASE_URL}/api/users/me`;
const MODELS_URL = `${TEST_API_BASE_URL}/api/models`;
const MCPS_URL = `${TEST_API_BASE_URL}/api/mcps`;

@Component({ template: 'chat' })
class ChatStub {}

@Component({ template: 'forbidden' })
class ForbiddenStub {}

@Component({ template: 'session error' })
class SessionErrorStub {}

@Component({ template: 'admin users' })
class AdminUsersStub {}

describe('adminCanMatch', () => {
  let backend: HttpTestingController;
  let loadAdmin: Mock<() => Promise<Routes>>;

  function configure(instance: IPublicClientApplication): void {
    loadAdmin = vi.fn((): Promise<Routes> =>
      Promise.resolve([{ path: '', component: AdminUsersStub }]),
    );

    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideFakeMsal(instance),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'chat', component: ChatStub },
          { path: 'forbidden', component: ForbiddenStub },
          { path: 'session-error', component: SessionErrorStub },
          { path: 'admin', canMatch: [adminCanMatch], loadChildren: loadAdmin },
          { path: '**', redirectTo: 'chat' },
        ]),
      ],
    });

    backend = TestBed.inject(HttpTestingController);
  }

  /** Answers the session bootstrap as the navigation asks for it. */
  async function answerBootstrap(user: UserDto): Promise<void> {
    await settle();
    backend.expectOne({ method: 'POST', url: ME_URL }).flush(user);
    await settle();
    backend.match(MODELS_URL).forEach((request) => request.flush([]));
    backend.match(MCPS_URL).forEach((request) => request.flush([]));
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    sessionStorage.clear();
  });

  it('never requests the admin chunk for a non-administrator', async () => {
    // The whole of US-203. `loadChildren` is what puts the chunk on the wire, so a
    // spy on it is the faithful, bundler-agnostic form of "not requested over the
    // network" — jsdom cannot observe a fetch that a real browser would make.
    configure(signedInMsal());
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/admin');
    await answerBootstrap(userFixture());
    await navigation;

    expect(loadAdmin).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/forbidden');
  });

  it('loads the chunk and renders the users tab for an administrator', async () => {
    configure(signedInMsal());
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/admin');
    await answerBootstrap(administratorFixture());
    await navigation;

    expect(loadAdmin).toHaveBeenCalledTimes(1);
    expect(TestBed.inject(Router).url).toBe('/admin');
    expect(harness.routeNativeElement?.textContent).toContain('admin users');
  });

  it('withholds the chunk from a signed-out visitor too', async () => {
    // No sign-in is started from here: handing the navigation to the chat route lets
    // authGuard own the redirect, and the chunk stays unrequested either way.
    configure(signedOutMsal());
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/admin');

    expect(loadAdmin).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/chat');
  });

  it('sends a failed session bootstrap to frame 6c, not to forbidden', async () => {
    // "We could not read your permissions" and "you do not have this permission" are
    // different answers, and showing the second for the first is a lie.
    configure(signedInMsal());
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/admin');
    await settle();
    backend.expectOne(ME_URL).flush('', { status: 503, statusText: 'Service Unavailable' });
    await navigation;

    expect(loadAdmin).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/session-error');
  });
});
