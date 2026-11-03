import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConversationActivityStore } from '../app/core/conversations/conversation-activity-store';

/** Connects a stand-in for the chat feature's session registry and returns its signals. */
export function connectActivity({
  running = [],
  runningCount = running.length,
  stop = () => {},
}: {
  running?: readonly string[];
  runningCount?: number;
  stop?: (conversationId: string) => void;
} = {}): {
  running: WritableSignal<ReadonlySet<string>>;
  runningCount: WritableSignal<number>;
} {
  const source = {
    running: signal<ReadonlySet<string>>(new Set(running)),
    runningCount: signal(runningCount),
  };
  TestBed.inject(ConversationActivityStore).connect({ ...source, viewing: signal(null), stop });

  return source;
}
