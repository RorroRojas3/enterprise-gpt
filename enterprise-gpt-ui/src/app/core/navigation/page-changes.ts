import { inject } from '@angular/core';
import { NavigationEnd, NavigationSkipped, NavigationSkippedCode, Router } from '@angular/router';
import { Observable, filter, map } from 'rxjs';

/** `page` is a new path; `same` is a link to where the reader already is. */
export type PageChange = 'page' | 'same';

function pathOf(url: string): string {
  return url.split(/[?#]/)[0] ?? url;
}

/**
 * A query change is the page updating itself, and a replacement is the URL catching up to a
 * conversation the reader is already in. Back and Forward are not: the router marks them
 * `replaceUrl` too, so the trigger tells them apart.
 */
export function injectPageChanges(): Observable<PageChange> {
  const router = inject(Router);
  let path = pathOf(router.url);

  return router.events.pipe(
    map((event): PageChange | null => {
      if (event instanceof NavigationSkipped) {
        return event.code === NavigationSkippedCode.IgnoredSameUrlNavigation ? 'same' : null;
      }
      if (!(event instanceof NavigationEnd)) {
        return null;
      }

      const previous = path;
      path = pathOf(event.urlAfterRedirects);
      const navigation = router.currentNavigation();
      const silent = navigation?.extras.replaceUrl === true && navigation.trigger !== 'popstate';

      return path !== previous && !silent ? 'page' : null;
    }),
    filter((change): change is PageChange => change !== null),
  );
}
