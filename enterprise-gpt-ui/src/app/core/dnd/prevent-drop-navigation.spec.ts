import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { dispatchDrag, fileDrag, textDrag, textFile } from '@testing/drag';
import { providePreventDropNavigation } from './prevent-drop-navigation';

describe('providePreventDropNavigation', () => {
  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [providePreventDropNavigation()] });
    // App initializers run when ApplicationInitStatus is first instantiated, not when
    // the testing module is configured.
    await TestBed.inject(ApplicationInitStatus).donePromise;
  });

  // On document.body: a live drop target stops propagation of the drops it handles.
  it('prevents a file dropped outside any drop target from replacing the app', () => {
    const event = dispatchDrag(document.body, 'drop', fileDrag(textFile('brief.txt')));

    expect(event.defaultPrevented).toBe(true);
  });

  it('marks an unclaimed file drag as "no drop" rather than lying with a copy cursor', () => {
    const event = dispatchDrag(document.body, 'dragover');

    expect(event.defaultPrevented).toBe(true);
    expect(event.dataTransfer?.dropEffect).toBe('none');
  });

  it('leaves a text drag entirely alone', () => {
    const over = dispatchDrag(document.body, 'dragover', textDrag());
    const drop = dispatchDrag(document.body, 'drop', textDrag());

    expect(over.defaultPrevented).toBe(false);
    expect(drop.defaultPrevented).toBe(false);
  });
});
