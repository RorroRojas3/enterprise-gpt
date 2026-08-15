/** The query parameter the projects grid filters on (US-901). */
export const NAME_PARAM = 'name';

/**
 * Re-exported so this screen's route contract reads from one file, as the
 * conversations library's does. The rule itself is shared — see
 * `core/navigation/search-history.ts`.
 */
export { shouldReplaceHistory } from '@core/navigation/search-history';
