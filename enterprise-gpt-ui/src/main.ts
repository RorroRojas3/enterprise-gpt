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
    // Overlaps the config fetch that gates bootstrap, so icons never pop in. loadIconSprite
    // never rejects, so it cannot reroute into the fatal shell.
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
    // Before bootstrap because no DI factory can await `initialize()`, so the injector only
    // ever holds a ready instance.
    msal = await createStandardPublicClientApplication(buildMsalConfig(config, document.baseURI));
  } catch (error) {
    console.error(error);
    // A malformed authority or client id fails here rather than at sign-in, where
    // Entra reports it as an opaque "application not found".
    showFatalShell('Sign-in could not be initialised for this deployment.');
    return;
  }

  try {
    // Resolved before bootstrap, not in an initializer, so every provider factory can read them.
    await bootstrapApplication(
      App,
      mergeApplicationConfig(appConfig, {
        providers: [
          { provide: APP_CONFIG, useValue: config },
          { provide: MSAL_INSTANCE, useValue: msal },
        ],
      }),
    );
    // The startup shell stays: bootstrap resolves before the first routed render, which
    // `provideStartupShellHandoff` waits for.
  } catch (error) {
    console.error(error);
    showFatalShell('The application failed to start.');
  }
}

void start();
