/**
 * The brand marks: the source of truth for their sprite symbols and `BrandIconName`. A lane
 * apart from {@link ICON_NAMES} because brand marks keep their owner's fills. A new name needs
 * `src/icons/brand/<name>.svg` and `npm run assets:icons`, or it is typed but renders blank.
 */
export const BRAND_ICON_NAMES = [
  'brand-azure',
  'brand-context7',
  'brand-github',
  'brand-microsoft',
  'brand-outlook',
  'brand-salesforce',
] as const;

/** A literal union, so `strictTemplates` rejects a misspelled `<app-brand-icon name>`. */
export type BrandIconName = (typeof BRAND_ICON_NAMES)[number];

/** Membership test for the dev-mode guard in `BrandIcon`, where the name is computed. */
export const BRAND_ICON_NAME_SET: ReadonlySet<string> = new Set<string>(BRAND_ICON_NAMES);
