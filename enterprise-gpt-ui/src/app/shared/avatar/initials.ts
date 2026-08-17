/** How many tones the avatar ramp offers. Matches the `--avatar-N-*` token pairs. */
export const AVATAR_TONE_COUNT = 4;

/**
 * The one or two letters an avatar shows for a person.
 *
 * Derived from the composed display name rather than from the separate first and last
 * name, because that is the string the server has already composed and a single-word
 * name has to fall back to one letter rather than render a stray comma.
 *
 * `fallback` covers the account Microsoft Graph gave neither a `givenName` nor a
 * `surname`: the API coalesces both to empty strings, so `fullName` is legitimately
 * empty and the email address is the only thing left to abbreviate.
 *
 * @param name The display name, e.g. `UserDto.fullName`.
 * @param fallback Used when `name` yields no letters at all — an email address.
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
 * Picks one of {@link AVATAR_TONE_COUNT} tones for a person, deterministically.
 *
 * Seeded on an identifier rather than on the name, so a rename does not recolour the
 * row and two people who share a name are still told apart at a glance. The hash is
 * the ordinary 31-multiplier one — this decides a colour, not a bucket in a hot path,
 * and anything stronger would be cost with no payer.
 *
 * @param seed A stable identifier, e.g. a user's object id.
 * @returns A 1-based tone number.
 */
export function avatarTone(seed: string): number {
  let hash = 0;
  for (let index = 0; index < seed.length; index += 1) {
    // `| 0` keeps this in int32 rather than drifting into float territory, which is
    // what makes the result identical on every engine.
    hash = (hash * 31 + seed.charCodeAt(index)) | 0;
  }

  return (Math.abs(hash) % AVATAR_TONE_COUNT) + 1;
}
