import { mergeApplicationConfig } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { createStandardPublicClientApplication } from '@azure/msal-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { MSAL_INSTANCE, buildMsalConfig } from './app/core/auth/msal-instance';
import { AppConfigError } from './app/core/config/app-config';
import { APP_CONFIG } from './app/core/config/app-config.token';
import { showFatalShell } from './app/core/config/fatal-shell';
import { loadAppConfig } from './app/core/config/load-app-config';
import { loadIconSprite } from './app/core/icons/load-icon-sprite';

async function start(): Promise<void> {
  let config;

  try {
    // The sprite fetch overlaps the config fetch that already gates bootstrap, so
    // it costs roughly nothing and guarantees no icon pop-in on first paint.
    // loadIconSprite never rejects, so it cannot reroute into the fatal shell.
    [config] = await Promise.all([loadAppConfig(), loadIconSprite()]);
  } catch (error) {
    console.error(error);
    // An AppConfigError message already names the offending field or transport
    // failure; anything else is a bug and should not be shown verbatim.
    showFatalShell(
      error instanceof AppConfigError ? error.message : 'The application configuration is invalid.',
    );
    return;
  }

  let msal;

  try {
    // Created and initialized here, before bootstrap, for the same reason APP_CONFIG
    // is: `initialize()` is asynchronous and no synchronous DI factory can await it,
    // so a factory would hand every consumer an instance that is not ready yet.
    // Resolving it here makes "MSAL_INSTANCE is initialized" a property of the
    // injector rather than something each caller has to remember.
    msal = await createStandardPublicClientApplication(buildMsalConfig(config, document.baseURI));
  } catch (error) {
    console.error(error);
    // A malformed authority or client id fails here rather than at sign-in, where
    // Entra reports it as an opaque "application not found".
    showFatalShell('Sign-in could not be initialised for this deployment.');
    return;
  }

  try {
    // The values are resolved before the call rather than inside an app initializer,
    // so both are genuinely available to every provider factory from the first moment
    // of the injector's life.
    await bootstrapApplication(
      App,
      mergeApplicationConfig(appConfig, {
        providers: [
          { provide: APP_CONFIG, useValue: config },
          { provide: MSAL_INSTANCE, useValue: msal },
        ],
      }),
    );
    // The startup shell is not removed here. Bootstrap resolves before the first
    // routed component renders, so `provideStartupShellHandoff` waits for that render
    // instead — otherwise an empty app shell paints over the startup card.
  } catch (error) {
    console.error(error);
    showFatalShell('The application failed to start.');
  }
}

void start();
