import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { projectDetailFixture } from '@testing/projects';
import { ProjectStore } from './project-store';

const FIRST = projectDetailFixture({ name: 'Helios' });
const SECOND = projectDetailFixture({ name: 'Atlas' });

function projectUrl(id: string): string {
  return `${TEST_API_BASE_URL}/api/projects/${id}`;
}

describe('ProjectStore', () => {
  let backend: HttpTestingController;
  let store: InstanceType<typeof ProjectStore>;
  let projectId: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideTestAppConfig(),
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectStore,
      ],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectStore);
    projectId = signal<string | null>(FIRST.id);
    TestBed.runInInjectionContext(() => store.bindRoute(projectId));
    TestBed.tick();
    backend.expectOne(projectUrl(FIRST.id)).flush(FIRST);
    TestBed.tick();
  });

  afterEach(() => {
    backend.verify();
  });

  it('clears the last project as soon as the route moves to another one', () => {
    projectId.set(SECOND.id);
    TestBed.tick();

    expect(store.project()).toBeNull();
    expect(store.isFirstLoad()).toBe(true);

    backend.expectOne(projectUrl(SECOND.id)).flush(SECOND);
    TestBed.tick();

    expect(store.project()).toEqual(SECOND);
    expect(store.isFirstLoad()).toBe(false);
  });

  it('keeps the project on screen while the same one is read again', () => {
    store.retry();
    TestBed.tick();

    expect(store.project()).toEqual(FIRST);
    expect(store.isFirstLoad()).toBe(false);

    backend.expectOne(projectUrl(FIRST.id)).flush(FIRST);
  });
});
