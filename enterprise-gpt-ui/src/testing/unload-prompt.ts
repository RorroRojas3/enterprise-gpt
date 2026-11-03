import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { UnloadPrompt } from '../app/core/navigation/unload-prompt';

/** Arms the leave guard as a streaming turn would. */
export function armUnloadPrompt(): void {
  const prompt = TestBed.inject(UnloadPrompt);
  TestBed.runInInjectionContext(() => prompt.register(signal(true)));
  TestBed.tick();
}

/** True when leaving now would make the browser ask first. */
export function wouldPromptOnUnload(): boolean {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}
