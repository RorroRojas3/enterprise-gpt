import { mergeApplicationConfig } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { AppConfigError } from './app/core/config/app-config';
import { APP_CONFIG } from './app/core/config/app-config.token';
import { hideStartupShell, showFatalShell } from './app/core/config/fatal-shell';
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

  try {
    // The value is resolved before the call rather than inside an app initializer,
    // so APP_CONFIG is genuinely available to every provider factory from the
    // first moment of the injector's life.
    await bootstrapApplication(
      App,
      mergeApplicationConfig(appConfig, {
        providers: [{ provide: APP_CONFIG, useValue: config }],
      }),
    );
    hideStartupShell();
  } catch (error) {
    console.error(error);
    showFatalShell('The application failed to start.');
  }
}

void start();
