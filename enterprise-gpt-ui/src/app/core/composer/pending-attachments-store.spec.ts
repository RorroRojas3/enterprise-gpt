import { TestBed } from '@angular/core/testing';
import { Dispatcher } from '@ngrx/signals/events';
import { beforeEach, describe, expect, it } from 'vitest';
import { sessionEvents } from '@core/events/session-events';
import { PendingAttachmentsStore } from './pending-attachments-store';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';
const OTHER_ID = '11111111-2222-4333-8444-555555555555';

function file(name: string): File {
  return new File(['x'], name, { type: 'text/plain' });
}

describe('PendingAttachmentsStore', () => {
  let store: InstanceType<typeof PendingAttachmentsStore>;

  beforeEach(() => {
    TestBed.resetTestingModule();
    store = TestBed.inject(PendingAttachmentsStore);
  });

  it('hands the files to the conversation they were held for', () => {
    store.hold(CONVERSATION_ID, [file('notes.txt'), file('report.md')]);

    expect(store.claim(CONVERSATION_ID).map((held) => held.name)).toEqual([
      'notes.txt',
      'report.md',
    ]);
  });

  it('gives them up once, so a refresh does not attach them twice', () => {
    store.hold(CONVERSATION_ID, [file('notes.txt')]);

    expect(store.claim(CONVERSATION_ID)).toHaveLength(1);
    expect(store.claim(CONVERSATION_ID)).toHaveLength(0);
  });

  it('withholds them from a conversation they were not meant for', () => {
    store.hold(CONVERSATION_ID, [file('notes.txt')]);

    // Files left over from an abandoned navigation must not fasten themselves onto
    // whatever the reader happens to open next.
    expect(store.claim(OTHER_ID)).toHaveLength(0);
    expect(store.claim(CONVERSATION_ID)).toHaveLength(1);
  });

  it('releases the previous user’s bytes on sign-out', () => {
    store.hold(CONVERSATION_ID, [file('notes.txt')]);

    TestBed.inject(Dispatcher).dispatch(sessionEvents.signedOut());

    expect(store.claim(CONVERSATION_ID)).toHaveLength(0);
  });
});
