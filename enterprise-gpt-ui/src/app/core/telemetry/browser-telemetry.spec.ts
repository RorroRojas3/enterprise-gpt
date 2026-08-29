import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router } from '@angular/router';
import { AuthService } from '@core/auth/auth-service';
import { AppConfig } from '@core/config/app-config';
import { APP_CONFIG } from '@core/config/app-config.token';
import type { IConfig, IConfiguration, ITelemetryItem } from '@microsoft/applicationinsights-web';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import devConfig from '../../../../public/config.json';
import {
  BrowserTelemetry,
  TELEMETRY_LOADER,
  TelemetryLoader,
  provideBrowserTelemetry,
} from './browser-telemetry';

const CONNECTION_STRING =
  'InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.in.applicationinsights.azure.com/';

const OBJECT_ID = 'e2d1c0b9-a8f7-4e6d-9c5b-4a3f2e1d0c9b';

interface Started {
  readonly config: IConfiguration & IConfig;
  readonly initializers: ((item: ITelemetryItem) => boolean | void)[];
  readonly pageViews: { uri: string }[];
  unloaded: boolean;
}

function config(telemetry?: { connectionString: string }): AppConfig {
  return { ...(devConfig as unknown as AppConfig), ...(telemetry ? { telemetry } : {}) };
}

function enabled(): AppConfig {
  return config({ connectionString: CONNECTION_STRING });
}

describe('provideBrowserTelemetry', () => {
  let started: Started[];
  let loader: TelemetryLoader;
  let loading: Promise<unknown>;
  let account: { localAccountId: string } | null;
  let events: Subject<NavigationEnd>;

  beforeEach(() => {
    started = [];
    account = null;
    loading = Promise.resolve();
    events = new Subject<NavigationEnd>();

    loader = (sdkConfig) => {
      const entry: Started = {
        config: sdkConfig,
        initializers: [],
        pageViews: [],
        unloaded: false,
      };
      started.push(entry);

      const telemetry: BrowserTelemetry = {
        addTelemetryInitializer: (initializer) => entry.initializers.push(initializer),
        trackPageView: (pageView) => entry.pageViews.push(pageView),
        unload: () => (entry.unloaded = true),
      };

      return (loading = Promise.resolve(telemetry));
    };
  });

  async function start(appConfig: AppConfig): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        { provide: APP_CONFIG, useValue: appConfig },
        { provide: TELEMETRY_LOADER, useValue: loader },
        {
          provide: AuthService,
          useValue: {
            get account() {
              return account;
            },
          },
        },
        { provide: Router, useValue: { events } },
        provideBrowserTelemetry(),
      ],
    });

    // App initializers run when ApplicationInitStatus is first instantiated, not when the
    // testing module is configured.
    await TestBed.inject(ApplicationInitStatus).donePromise;
    // The provider starts the load without awaiting it, on purpose, so the spec awaits the
    // loader's own promise rather than guessing at a number of microtasks.
    await loading.catch(() => undefined);
  }

  function initialize(item: ITelemetryItem): void {
    for (const initializer of started[0]?.initializers ?? []) {
      // A telemetry initializer that returns false drops the item, which would silently
      // discard everything this one is here to enrich.
      expect(initializer(item)).not.toBe(false);
    }
  }

  it('loads nothing when config.json carries no telemetry block', async () => {
    await start(config());

    expect(started).toHaveLength(0);
  });

  it('starts the SDK when a telemetry block is present', async () => {
    await start(enabled());

    expect(started).toHaveLength(1);
    expect(started[0]?.config.connectionString).toBe(CONNECTION_STRING);
  });

  it('counts the landing page, which the SDK itself never emits', async () => {
    await start(enabled());

    expect(started[0]?.pageViews).toHaveLength(1);
  });

  it('counts one page view per completed navigation', async () => {
    await start(enabled());

    events.next(new NavigationEnd(1, '/chat', '/chat'));
    events.next(new NavigationEnd(2, '/projects', '/projects'));

    expect(started[0]?.pageViews).toHaveLength(3);
  });

  /**
   * The SDK's own route hook fires per history mutation, which the sign-in path performs
   * three times for one arrival.
   */
  it('leaves the SDK route hook off', async () => {
    await start(enabled());

    expect(started[0]?.config.enableAutoRouteTracking).toBe(false);
  });

  it('allows correlation headers on the API host, anchored so a suffix cannot match', async () => {
    await start(enabled());

    expect(started[0]?.config.enableCorsCorrelation).toBe(true);
    expect(started[0]?.config.correlationHeaderDomains).toEqual(['^localhost:7045$']);

    const [pattern] = started[0]?.config.correlationHeaderDomains ?? [];
    expect(new RegExp(pattern!).test('localhost:7045.attacker.example')).toBe(false);
  });

  it('sets no cookies', async () => {
    await start(enabled());

    expect(started[0]?.config.disableCookiesUsage).toBe(true);
  });

  it('attributes an item to the signed-in Entra object id', async () => {
    account = { localAccountId: OBJECT_ID };
    await start(enabled());

    const item: ITelemetryItem = { name: 'PageView' };
    initialize(item);

    expect(item.tags?.['ai.user.authUserId']).toBe(OBJECT_ID);
  });

  it('leaves an item unattributed while nobody is signed in', async () => {
    await start(enabled());

    const item: ITelemetryItem = { name: 'PageView' };
    initialize(item);

    expect(item.tags?.['ai.user.authUserId']).toBeUndefined();
  });

  /** The fragment carries the authorization code; the query string carries search terms. */
  it.each([
    [
      'uri',
      'https://app.example.com/auth#code=SECRET&client_info=abc',
      'https://app.example.com/auth',
    ],
    ['refUri', 'https://app.example.com/chat?query=confidential', 'https://app.example.com/chat'],
    [
      'data',
      'https://api.example.com/api/conversations?query=confidential',
      'https://api.example.com/api/conversations',
    ],
  ])('strips the query string and fragment from %s', async (field, value, expected) => {
    await start(enabled());

    const item: ITelemetryItem = { name: 'PageView', baseData: { [field]: value } };
    initialize(item);

    expect(item.baseData?.[field]).toBe(expected);
  });

  it('leaves an item with no baseData alone', async () => {
    await start(enabled());

    const item: ITelemetryItem = { name: 'Exception' };

    expect(() => initialize(item)).not.toThrow();
  });

  it('registers exactly one initializer', async () => {
    await start(enabled());

    expect(started[0]?.initializers).toHaveLength(1);
  });

  it('unloads the SDK when the injector is destroyed, so its patches do not outlive it', async () => {
    await start(enabled());
    TestBed.resetTestingModule();

    expect(started[0]?.unloaded).toBe(true);
  });

  it('reports a failed load without letting it reject', async () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const rejected = Promise.reject<BrowserTelemetry>(new Error('offline'));
    loading = rejected;
    loader = () => rejected;

    await start(enabled());
    await Promise.resolve();

    expect(error).toHaveBeenCalled();
    error.mockRestore();
  });
});
