import { TestBed } from '@angular/core/testing';
import { AuthService } from '@core/auth/auth-service';
import { SIGNED_OUT_ROUTE } from '@core/auth/auth-routes';
import { HardNavigation } from '@core/navigation/hard-navigation';
import { Events } from '@ngrx/signals/events';
import { sessionEvents } from '@core/events/session-events';
import { provideTestAppConfig } from '@testing/app-config';
import { settle } from '@testing/async';
import { FakeNavigation, provideFakeNavigation } from '@testing/navigation';
import { provideFakeMsal, signedInMsal } from '@testing/msal';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionChannel } from './session-channel';

const CHANNEL_NAME = 'egpt.session';

describe('SessionChannel', () => {
  /** Stands in for the other tab. jsdom delivers between two channels of the same name. */
  let otherTab: BroadcastChannel;

  beforeEach(() => {
    TestBed.resetTestingModule();
    otherTab = new BroadcastChannel(CHANNEL_NAME);
  });

  afterEach(() => otherTab.close());

  it('carries a sign-out to another tab', async () => {
    const received = vi.fn();
    otherTab.addEventListener('message', (event: MessageEvent<unknown>) => received(event.data));

    TestBed.configureTestingModule({});
    TestBed.inject(SessionChannel).post();
    await settle();

    expect(received).toHaveBeenCalledWith('signed-out');
  });

  it('ignores traffic that is not a sign-out', async () => {
    const onSignedOut = vi.fn();
    TestBed.configureTestingModule({});
    TestBed.inject(SessionChannel).listen(onSignedOut);

    otherTab.postMessage('something-else');
    await settle();

    expect(onSignedOut).not.toHaveBeenCalled();
  });

  describe('a tab told that another has signed out', () => {
    function navigation(): FakeNavigation {
      return TestBed.inject(HardNavigation) as unknown as FakeNavigation;
    }

    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [
          provideTestAppConfig(),
          provideFakeMsal(signedInMsal({ clearCache: () => Promise.resolve() })),
          provideFakeNavigation(),
        ],
      });
    });

    it('tears its own session down and leaves', async () => {
      // MSAL's cacheLocation is sessionStorage, which is per tab: without this, tab B
      // keeps its own token cache and its own populated stores after tab A signs out.
      const auth = TestBed.inject(AuthService);
      const seen = vi.fn();
      TestBed.inject(Events).on(sessionEvents.signedOut).subscribe(seen);

      otherTab.postMessage('signed-out');
      await settle();

      expect(seen).toHaveBeenCalledOnce();
      expect(auth.isSigningOut()).toBe(true);
      expect(navigation().assign).toHaveBeenCalledWith(SIGNED_OUT_ROUTE);
    });

    it('does not echo, so two tabs cannot bounce a sign-out between them', async () => {
      const echoes = vi.fn();
      TestBed.inject(AuthService);
      otherTab.addEventListener('message', (event: MessageEvent<unknown>) => echoes(event.data));

      otherTab.postMessage('signed-out');
      await settle();

      expect(echoes).not.toHaveBeenCalled();
    });
  });
});
