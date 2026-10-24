import { InjectionToken, Provider } from '@angular/core';
import { UploadStore } from './upload-store';

/** The composer's own view of {@link UploadStore}, whichever instance it is pointed at. */
export type ComposerUploads = InstanceType<typeof UploadStore>;

export const COMPOSER_UPLOADS = new InjectionToken<ComposerUploads>('COMPOSER_UPLOADS');

/**
 * A separate store, for the project route: the panel's files are the project's, while the
 * composer's belong to the conversation it is about to create.
 */
export function provideComposerUploads(): Provider {
  return { provide: COMPOSER_UPLOADS, useClass: UploadStore };
}

/**
 * Shares the injector's `UploadStore`, for chat: `TurnStore` must gate its stream on the very
 * chips the composer draws.
 */
export function provideSharedComposerUploads(): Provider {
  return { provide: COMPOSER_UPLOADS, useExisting: UploadStore };
}
