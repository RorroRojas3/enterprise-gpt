/**
 * The multi-colour brand marks this application ships, and — like
 * {@link ICON_NAMES} for the monochrome set — the single source of truth for the
 * sprite `scripts/build-icon-sprite.mjs` emits and for the `BrandIconName` type.
 *
 * Two lanes rather than one list because the two are built differently: a
 * Bootstrap Icons glyph is rewritten to `fill="currentColor"` so one symbol
 * serves both themes, while a brand mark must keep the fills its owner
 * specifies. Sources for these live beside this file in `brand/`, not in
 * `node_modules`.
 *
 * Adding a name here is not enough on its own — add `brand/<name>.svg` and
 * re-run `npm run assets:icons`, or the mark will be typed but absent from the
 * sprite and render blank.
 */
export const BRAND_ICON_NAMES = ['brand-context7', 'brand-microsoft'] as const;

/**
 * Every brand mark in the sprite, as a literal union. `strictTemplates` rejects
 * a misspelled `<app-brand-icon name="…">` at compile time because of this type.
 */
export type BrandIconName = (typeof BRAND_ICON_NAMES)[number];

/** Membership test for the dev-mode guard in `BrandIcon`, where the name is computed. */
export const BRAND_ICON_NAME_SET: ReadonlySet<string> = new Set<string>(BRAND_ICON_NAMES);
