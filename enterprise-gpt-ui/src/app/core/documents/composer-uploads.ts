import { InjectionToken, Provider } from '@angular/core';
import { UploadStore } from './upload-store';

/** The composer's own view of {@link UploadStore}, whichever instance it is pointed at. */
export type ComposerUploads = InstanceType<typeof UploadStore>;

export const COMPOSER_UPLOADS = new InjectionToken<ComposerUploads>('COMPOSER_UPLOADS');

/**
 * Gives the composer an upload store of its own, separate from the screen's.
 *
 * For a screen whose own `UploadStore` belongs to something else — the project detail
 * route, where the panel's files are the *project's* and the composer's belong to the
 * conversation its prompt is about to create.
 */
export function provideComposerUploads(): Provider {
  return { provide: COMPOSER_UPLOADS, useClass: UploadStore };
}

/**
 * Points the composer at the `UploadStore` already provided on the same injector.
 *
 * For the chat route, where the chips the composer draws must be the very ones
 * `TurnStore` gates its stream on — two instances would let a turn open while the file
 * it is about is still being ingested.
 */
export function provideSharedComposerUploads(): Provider {
  return { provide: COMPOSER_UPLOADS, useExisting: UploadStore };
}
