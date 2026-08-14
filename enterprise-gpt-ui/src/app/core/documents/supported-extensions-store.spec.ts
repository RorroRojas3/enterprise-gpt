import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { SupportedExtensionsStore, extensionOf } from './supported-extensions-store';

const URL = `${TEST_API_BASE_URL}/api/documents/file-extensions`;
const SUPPORTED = ['.doc', '.docx', '.md', '.pdf', '.pptx', '.txt'];

describe('extensionOf', () => {
  it('reads a dotted, lower-cased extension', () => {
    expect(extensionOf('Q3-Report.PDF')).toBe('.pdf');
    expect(extensionOf('archive.tar.gz')).toBe('.gz');
  });

  it('treats a dotfile and an extensionless name as having none', () => {
    // The server parses the same way and refuses both.
    expect(extensionOf('.gitignore')).toBe('');
    expect(extensionOf('LICENSE')).toBe('');
  });
});

describe('SupportedExtensionsStore', () => {
  let store: InstanceType<typeof SupportedExtensionsStore>;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);
    store = TestBed.inject(SupportedExtensionsStore);
  });

  afterEach(() => backend.verify());

  it('loads once per session however many callers ask', async () => {
    const first = store.ensureLoaded();
    const second = store.ensureLoaded();
    backend.expectOne(URL).flush(SUPPORTED);
    await Promise.all([first, second]);

    expect(store.extensions()).toEqual(SUPPORTED);
    expect(store.accept()).toBe('.doc,.docx,.md,.pdf,.pptx,.txt');
    expect(store.supportedLabel()).toBe('DOC, DOCX, MD, PDF, PPTX, TXT');
  });

  it('accepts what the server sends, case-normalised', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne(URL).flush(['.PDF', '.Txt']);
    await loaded;

    expect(store.isSupported('report.pdf')).toBe(true);
    expect(store.isSupported('notes.TXT')).toBe(true);
    expect(store.isSupported('demo.mov')).toBe(false);
  });

  it('lets everything through when the list could not be loaded', async () => {
    const loaded = store.ensureLoaded();
    backend.expectOne(URL).error(new ProgressEvent('error'));
    await loaded;

    // Refusing every file because a side request failed would be worse than the
    // round trip it saves: the server refuses it with a real reason (US-805).
    expect(store.error()).not.toBeNull();
    expect(store.isSupported('demo.mov')).toBe(true);
    expect(store.accept()).toBe('');
  });
});
