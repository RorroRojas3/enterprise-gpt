import { PathKind, SchemaPath, SchemaPathRules, validate } from '@angular/forms/signals';

/** `required` accepts spaces and every submit path trims, so blank needs its own message. */
export function notBlank<TPathKind extends PathKind = PathKind.Root>(
  path: SchemaPath<string, SchemaPathRules.Supported, TPathKind>,
  message: string,
): void {
  validate(path, ({ value }) =>
    value() !== '' && value().trim() === '' ? { kind: 'blank', message } : null,
  );
}
