import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeRecognition } from '@testing/speech';
import { SPEECH_RECOGNITION, SpeechRecognitionLike } from '@core/speech/speech-recognition';
import { DictationStore } from './dictation-store';

describe('DictationStore (US-413)', () => {
  let recognition: FakeRecognition;
  let store: InstanceType<typeof DictationStore>;

  function setup(factory: (() => SpeechRecognitionLike) | null): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [{ provide: SPEECH_RECOGNITION, useValue: factory }, DictationStore],
    });
    store = TestBed.inject(DictationStore);
  }

  beforeEach(() => {
    vi.useFakeTimers();
    recognition = new FakeRecognition();
    setup(() => recognition);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('reports the browser as unable to dictate when there is no engine', () => {
    setup(null);

    expect(store.supported()).toBe(false);
    store.start();
    expect(store.recording()).toBe(false);
  });

  it('starts a session configured for whole phrases, not interim guesses', () => {
    store.start();

    expect(store.recording()).toBe(true);
    expect(recognition.starts).toBe(1);
    expect(recognition.continuous).toBe(true);
    // Interim results rewrite themselves word by word; the prompt box is the
    // wrong place to watch that happen.
    expect(recognition.interimResults).toBe(false);
    expect(recognition.lang).toBe(navigator.language);
  });

  it('ignores a second start while a session is running', () => {
    store.start();
    store.start();

    expect(recognition.starts).toBe(1);
  });

  it('publishes finalized phrases only', () => {
    store.start();

    recognition.say({ text: 'What is the', isFinal: false });
    expect(store.spoken()).toBeNull();

    recognition.say({ text: 'What is the weather', isFinal: true });
    expect(store.spoken()).toEqual({ text: 'What is the weather' });

    store.consumeSpoken();
    expect(store.spoken()).toBeNull();
  });

  it('counts the session in the frame 2i clock and resets it when the session ends', () => {
    store.start();
    expect(store.elapsedLabel()).toBe('0:00');

    vi.advanceTimersByTime(67_000);
    expect(store.elapsedLabel()).toBe('1:07');

    store.stop();
    expect(store.recording()).toBe(false);
    expect(store.elapsedLabel()).toBe('0:00');

    // The clock stops with the session rather than running on invisibly.
    vi.advanceTimersByTime(5000);
    expect(store.elapsedLabel()).toBe('0:00');
  });

  it('stops rather than aborts, so the last phrase spoken is not discarded', () => {
    store.start();
    store.stop();

    expect(recognition.stops).toBe(1);
    expect(recognition.aborts).toBe(0);
  });

  it('records a refused microphone, and says nothing about failures the user cannot fix', () => {
    store.start();
    recognition.fail('not-allowed');

    expect(store.permissionDenied()).toBe(true);
    expect(store.recording()).toBe(false);

    store.start();
    expect(store.permissionDenied()).toBe(false);
    recognition.fail('audio-capture');
    expect(store.permissionDenied()).toBe(false);
    expect(store.recording()).toBe(false);
  });

  it('survives an engine that refuses to start', () => {
    recognition.start = () => {
      throw new DOMException('already started', 'InvalidStateError');
    };

    store.start();

    // Nothing started, so nothing is left recording or ticking.
    expect(store.recording()).toBe(false);
    vi.advanceTimersByTime(3000);
    expect(store.elapsedSeconds()).toBe(0);
  });

  it('releases the microphone when the screen goes away', () => {
    store.start();

    TestBed.resetTestingModule();

    expect(recognition.aborts).toBe(1);
  });
});
