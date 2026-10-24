import { UploadTarget } from '@core/api/document.model';

/** The `api/`-relative path segment a target's document routes live under. */
export function documentTargetPath(target: UploadTarget): string {
  return target.kind === 'conversation' ? 'conversations' : 'projects';
}
