/** Matches the `--avatar-N-*` token pairs. */
export const AVATAR_TONE_COUNT = 4;

/**
 * From the composed display name, so a single-word name yields one letter. `fallback` (the email)
 * covers Graph accounts with no given name or surname, whose `fullName` is empty.
 */
export function initialsOf(name: string, fallback = ''): string {
  const letters = name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('');

  return letters === '' ? (fallback.trim()[0]?.toUpperCase() ?? '') : letters;
}

/**
 * Seed on a stable id, so a rename does not recolour and namesakes differ. A plain 31-multiplier
 * hash is enough to pick a colour.
 *
 * @returns A 1-based tone number.
 */
export function avatarTone(seed: string): number {
  let hash = 0;
  for (let index = 0; index < seed.length; index += 1) {
    // `| 0` keeps int32 arithmetic, so every engine gives the same tone.
    hash = (hash * 31 + seed.charCodeAt(index)) | 0;
  }

  return (Math.abs(hash) % AVATAR_TONE_COUNT) + 1;
}
