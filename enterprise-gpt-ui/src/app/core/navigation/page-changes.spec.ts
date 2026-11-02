import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { filter, firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { PageChange, injectPageChanges } from './page-changes';

@Component({
  selector: 'app-blank',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '',
})
class Blank {}

describe('injectPageChanges', () => {
  let changes: PageChange[];
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '**', component: Blank }]), provideLocationMocks()],
    });

    await RouterTestingHarness.create('/chat');
    router = TestBed.inject(Router);
    // The harness navigates directly and never starts the popstate listener Back depends on.
    router.setUpLocationChangeListener();
    changes = [];
    TestBed.runInInjectionContext(injectPageChanges).subscribe((change) => changes.push(change));
  });

  it('takes the path it is created on as where the reader already is', async () => {
    await router.navigateByUrl('/chat?draft=1');

    expect(changes).toEqual([]);
  });

  it('reports a new path as a page change', async () => {
    await router.navigateByUrl('/documents');

    expect(changes).toEqual(['page']);
  });

  it('ignores a query change, which is the page updating itself', async () => {
    await router.navigateByUrl('/documents');
    changes.length = 0;

    await router.navigateByUrl('/documents?page=2');

    expect(changes).toEqual([]);
  });

  it('ignores a replaced URL, and still tracks it as the current path', async () => {
    await router.navigateByUrl('/chat/abc', { replaceUrl: true });

    expect(changes).toEqual([]);

    await router.navigateByUrl('/documents');
    await router.navigateByUrl('/chat/abc');

    expect(changes).toEqual(['page', 'page']);
  });

  it('reports Back as a page change, though the router marks it as a replacement', async () => {
    await router.navigateByUrl('/documents');
    changes.length = 0;

    TestBed.inject(Location).back();
    await firstValueFrom(router.events.pipe(filter((event) => event instanceof NavigationEnd)));

    expect(router.url).toBe('/chat');
    expect(changes).toEqual(['page']);
  });

  it('reports a navigation to the current URL as staying put', async () => {
    await router.navigateByUrl('/chat');

    expect(changes).toEqual(['same']);
  });
});
