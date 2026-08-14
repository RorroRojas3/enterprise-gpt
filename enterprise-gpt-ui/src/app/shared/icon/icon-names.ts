/**
 * The Bootstrap Icons glyphs this application ships, and the single source of
 * truth for three things: the sprite `scripts/build-icon-sprite.mjs` emits, the
 * `IconName` template type, and the build check in `scripts/check-icon-names.mjs`.
 *
 * 75 entries: the 73 distinct glyphs the design boards under `docs/design/project/`
 * reference — the PRD's figure of 60 is stale — plus `bi-file-earmark`, which the
 * attachment chip falls back to for an extension it does not recognise, and
 * `bi-file-earmark-ppt`, which the boards never drew because their example set omits
 * `.pptx` — a format the API does accept. All 75 exist in bootstrap-icons 1.13.1.
 *
 * Adding a name here is not enough on its own — re-run `npm run assets:icons`, or
 * the glyph will be typed but absent from the sprite and render blank.
 */
export const ICON_NAMES = [
  'bi-arrow-clockwise',
  'bi-arrow-down',
  'bi-arrow-down-right',
  'bi-arrow-right',
  'bi-arrow-up-circle',
  'bi-arrow-up-right',
  'bi-box-arrow-right',
  'bi-calendar3',
  'bi-chat-left',
  'bi-chat-square-text',
  'bi-check-circle-fill',
  'bi-check-lg',
  'bi-check-square-fill',
  'bi-chevron-down',
  'bi-chevron-left',
  'bi-chevron-right',
  'bi-chevron-up',
  'bi-clock-history',
  'bi-code-slash',
  'bi-copy',
  'bi-cpu',
  'bi-dash-circle',
  'bi-dash-square-fill',
  'bi-download',
  'bi-exclamation-octagon',
  'bi-exclamation-triangle-fill',
  'bi-file-earmark',
  'bi-file-earmark-arrow-up',
  'bi-file-earmark-excel',
  'bi-file-earmark-pdf',
  'bi-file-earmark-ppt',
  'bi-file-earmark-text',
  'bi-file-earmark-word',
  'bi-file-text',
  'bi-filetype-csv',
  'bi-filetype-md',
  'bi-folder',
  'bi-folder-fill',
  'bi-folder-minus',
  'bi-folder-symlink',
  'bi-folder2',
  'bi-graph-up',
  'bi-hand-thumbs-down',
  'bi-hand-thumbs-up',
  'bi-hourglass-split',
  'bi-info-circle',
  'bi-kanban',
  'bi-list',
  'bi-lock',
  'bi-mic',
  'bi-mic-fill',
  'bi-moon',
  'bi-paperclip',
  'bi-pencil',
  'bi-pencil-square',
  'bi-people',
  'bi-plug',
  'bi-plus-lg',
  'bi-question-circle',
  'bi-search',
  'bi-send-fill',
  'bi-shield-lock',
  'bi-slash-circle',
  'bi-sliders',
  'bi-square',
  'bi-star',
  'bi-star-fill',
  'bi-stop-circle',
  'bi-sun',
  'bi-three-dots-vertical',
  'bi-tools',
  'bi-trash3',
  'bi-x',
  'bi-x-circle-fill',
  'bi-x-lg',
] as const;

/**
 * Every glyph in the sprite, as a literal union. `strictTemplates` rejects a
 * misspelled `<app-icon name="…">` at compile time because of this type; the build
 * check exists only for the references the type system cannot see, such as a raw
 * `class="bi bi-…"` in a template.
 */
export type IconName = (typeof ICON_NAMES)[number];

/** Membership test for the dev-mode guard in `Icon`, where the name is computed. */
export const ICON_NAME_SET: ReadonlySet<string> = new Set<string>(ICON_NAMES);
