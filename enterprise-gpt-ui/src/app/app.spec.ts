import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { App } from './app';

describe('App', () => {
  it('creates the root component', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });

    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();

    expect(fixture.componentInstance).toBeInstanceOf(App);
  });

  it('runs without zone.js loaded', () => {
    // Behavioural proof of the zoneless AC: no polyfill entry means no Zone global,
    // which a grep over angular.json cannot establish.
    expect((globalThis as Record<string, unknown>)['Zone']).toBeUndefined();
  });
});
