import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TEST_API_BASE_URL, provideTestAppConfig } from '@testing/app-config';
import { MessageAttachmentDto } from '@domain/api/conversation';
import { DocumentDownloadDto } from '@domain/api/document';
import { GeneratedFiles } from './generated-files';

const CONVERSATION_ID = '6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b';

const SPREADSHEET: MessageAttachmentDto = {
  id: '9c8b7a65-4321-4fed-8cba-0987654321fe',
  name: 'regional-summary.xlsx',
  extension: '.xlsx',
  mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  size: 2 * 1024 * 1024,
};

const DECK: MessageAttachmentDto = {
  id: '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d',
  name: 'meeting-summary.pptx',
  extension: '.pptx',
  mimeType: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  size: 51_200,
};

const SIGNED: DocumentDownloadDto = {
  downloadUrl: 'https://acct.blob.core.windows.net/generated-documents/8f3/x.xlsx?sig=secret',
  fileName: 'regional-summary.xlsx',
  expiresAt: '2026-08-28T06:00:00+00:00',
};

function downloadUrl(documentId: string): string {
  return `${TEST_API_BASE_URL}/api/documents/conversations/${CONVERSATION_ID}/${documentId}`;
}

describe('GeneratedFiles', () => {
  let fixture: ComponentFixture<GeneratedFiles>;
  let host: HTMLElement;
  let backend: HttpTestingController;
  let clicks: HTMLAnchorElement[];

  beforeEach(async () => {
    clicks = [];
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideTestAppConfig(), provideHttpClient(), provideHttpClientTesting()],
    });

    backend = TestBed.inject(HttpTestingController);

    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicks.push(this);
    });

    fixture = TestBed.createComponent(GeneratedFiles);
    host = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('conversationId', CONVERSATION_ID);
    fixture.componentRef.setInput('attachments', [SPREADSHEET]);
    await fixture.whenStable();
  });

  afterEach(() => {
    backend.verify();
    vi.restoreAllMocks();
  });

  it('shows the file name, its extension glyph and its size', async () => {
    expect(host.querySelector('.chip__name')?.textContent?.trim()).toBe('regional-summary.xlsx');
    expect(host.querySelector('app-icon use')?.getAttribute('href')).toBe('#bi-file-earmark-excel');
    expect(host.textContent).toContain('2 MB');
  });

  it('says the file was generated, in text as well as by its glyph', async () => {
    // Colour and an icon alone would not reach a reader who cannot see either.
    expect(host.querySelector('.source-badge')?.textContent?.trim()).toBe('Generated');
    expect(host.querySelector('.chip')?.getAttribute('aria-label')).toBe(
      'Generated file, regional-summary.xlsx',
    );
  });

  it('does not offer to remove a file the assistant made', async () => {
    // There is nothing to cancel and nothing to dismiss: it is already stored.
    expect(host.querySelector('.chip__remove')).toBeNull();
  });

  it('asks for no link until the chip is clicked', () => {
    backend.expectNone(downloadUrl(SPREADSHEET.id));
  });

  it('mints a link on click and hands it straight to the browser', async () => {
    host.querySelector<HTMLButtonElement>('.chip__action')?.click();
    await fixture.whenStable();

    backend.expectOne(downloadUrl(SPREADSHEET.id)).flush(SIGNED);
    await fixture.whenStable();

    expect(clicks[0]?.href).toBe(SIGNED.downloadUrl);
    expect(clicks[0]?.download).toBe('regional-summary.xlsx');
  });

  it('leaves the signed link nowhere it could outlive its few minutes', async () => {
    host.querySelector<HTMLButtonElement>('.chip__action')?.click();
    await fixture.whenStable();
    backend.expectOne(downloadUrl(SPREADSHEET.id)).flush(SIGNED);
    await fixture.whenStable();

    expect(host.innerHTML).not.toContain('sig=secret');
    expect(localStorage.getItem('x')).toBeNull();
    expect(window.location.href).not.toContain('sig=secret');
  });

  it('spins only the chip being downloaded', async () => {
    fixture.componentRef.setInput('attachments', [SPREADSHEET, DECK]);
    await fixture.whenStable();

    const buttons = [...host.querySelectorAll<HTMLButtonElement>('.chip__action')];
    buttons[0]?.click();
    await fixture.whenStable();

    expect(buttons[0]?.getAttribute('aria-busy')).toBe('true');
    expect(buttons[1]?.getAttribute('aria-busy')).toBeNull();

    backend.expectOne(downloadUrl(SPREADSHEET.id)).flush(SIGNED);
    await fixture.whenStable();
  });

  it('renders nothing at all when the turn produced no file', async () => {
    fixture.componentRef.setInput('attachments', []);
    await fixture.whenStable();

    expect(host.querySelector('.chip')).toBeNull();
  });
});
