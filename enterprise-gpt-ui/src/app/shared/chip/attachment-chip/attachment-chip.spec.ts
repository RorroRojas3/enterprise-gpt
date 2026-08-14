import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AttachmentChip } from './attachment-chip';
import { Attachment, AttachmentState, fileGlyph } from './attachment.model';

function attachment(state: AttachmentState): Attachment {
  return { id: 'a1', fileName: 'q3-report.pdf', sizeLabel: '2.4 MB', state };
}

describe('fileGlyph', () => {
  it.each([
    ['report.pdf', 'bi-file-earmark-pdf', 'fail'],
    ['sheet.XLSX', 'bi-file-earmark-excel', 'ok'],
    ['notes.md', 'bi-filetype-md', 'accent'],
    ['data.csv', 'bi-filetype-csv', 'accent'],
    ['deck.pptx', 'bi-file-earmark-ppt', 'warn'],
  ])('maps %s to its glyph', (fileName, icon, tone) => {
    expect(fileGlyph(fileName)).toEqual({ icon, tone });
  });

  it('falls back rather than throwing on an unknown or absent extension', () => {
    expect(fileGlyph('archive.har').icon).toBe('bi-file-earmark');
    expect(fileGlyph('LICENSE').icon).toBe('bi-file-earmark');
  });
});

describe('AttachmentChip', () => {
  beforeEach(() => TestBed.configureTestingModule({}));

  async function render(state: AttachmentState) {
    const fixture = TestBed.createComponent(AttachmentChip);
    fixture.componentRef.setInput('attachment', attachment(state));
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('shows uploading as measurable progress, not just a spinner', async () => {
    const { host } = await render({ kind: 'uploading', percent: 42 });

    const bar = host.querySelector('[role="progressbar"]');
    expect(bar?.getAttribute('aria-valuenow')).toBe('42');
    expect(bar?.getAttribute('aria-label')).toBe('Uploading q3-report.pdf');
    expect(host.textContent).toContain('42%');
  });

  it('shows processing with its sub-status in a polite live region', async () => {
    const { host } = await render({
      kind: 'processing',
      subStatus: 'Generating embeddings… 3 of 12 pages',
    });

    expect(host.querySelector('[role="status"]')?.textContent).toContain('3 of 12 pages');
    expect(host.querySelector('.kit-spinner')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('shows ready with its size', async () => {
    const { host } = await render({ kind: 'ready' });

    expect(host.textContent).toContain('2.4 MB');
    expect(host.textContent).toContain('Ready to use');
    expect(host.querySelector('[role="progressbar"]')).toBeNull();
  });

  it('shows a failed job with its reason and a named retry', async () => {
    const { fixture, host } = await render({
      kind: 'failed',
      reason: 'The document could not be read. It may be corrupt or password-protected.',
    });
    const retried = vi.fn();
    fixture.componentInstance.retried.subscribe(retried);

    const retry = host.querySelector<HTMLButtonElement>('.chip__retry');
    expect(retry?.getAttribute('aria-label')).toBe('Retry uploading q3-report.pdf');
    expect(host.textContent).toContain('could not be read');

    retry?.click();
    expect(retried).toHaveBeenCalledWith('a1');
  });

  it('names the refused extension and the supported set, and offers no retry', async () => {
    // The same file through the same pipeline will be refused again, so the control
    // frame 4l draws here would be a button that cannot work — see `canRetry`.
    const { host } = await render({
      kind: 'unsupported',
      reason: '.mov isn’t supported — PDF, DOCX, MD, PPTX, TXT',
    });

    expect(host.textContent).toContain('isn’t supported');
    expect(host.textContent).toContain('PPTX');
    expect(host.querySelector('.chip__retry')).toBeNull();
  });

  it('renders expired-or-unknown differently from failed, and offers no retry', async () => {
    const { host } = await render({ kind: 'unknown' });

    expect(host.textContent).toContain('Status is only kept for a limited time.');
    expect(host.textContent?.toLowerCase()).not.toContain('failed');
    expect(host.querySelector('.chip__retry')).toBeNull();
  });

  it('gives the remove control a name that says what it removes', async () => {
    const { host } = await render({ kind: 'ready' });

    expect(host.querySelector('.chip__remove')?.getAttribute('aria-label')).toBe(
      'Remove q3-report.pdf',
    );
  });

  it('says "dismiss" once there is nothing left to cancel', async () => {
    // No API detaches a conversation document, so the control only hides the chip
    // from here on. Calling it "Remove" would claim an effect it does not have.
    const fixture = TestBed.createComponent(AttachmentChip);
    fixture.componentRef.setInput('attachment', attachment({ kind: 'ready' }));
    fixture.componentRef.setInput('removeAction', 'dismiss');
    await fixture.whenStable();

    expect(
      (fixture.nativeElement as HTMLElement)
        .querySelector('.chip__remove')
        ?.getAttribute('aria-label'),
    ).toBe('Dismiss q3-report.pdf');
  });

  it('offers download only where the caller says the file can be fetched back', async () => {
    const plain = await render({ kind: 'ready' });
    expect(plain.host.querySelector('[aria-label^="Download"]')).toBeNull();

    const fixture = TestBed.createComponent(AttachmentChip);
    fixture.componentRef.setInput('attachment', attachment({ kind: 'ready' }));
    fixture.componentRef.setInput('downloadable', true);
    await fixture.whenStable();
    const downloaded = vi.fn();
    fixture.componentInstance.downloaded.subscribe(downloaded);

    const download = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[aria-label="Download q3-report.pdf"]',
    );
    download?.click();

    expect(downloaded).toHaveBeenCalledWith('a1');
  });

  it('hides the remove control where the caller forbids it', async () => {
    const fixture = TestBed.createComponent(AttachmentChip);
    fixture.componentRef.setInput('attachment', attachment({ kind: 'ready' }));
    fixture.componentRef.setInput('removable', false);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('.chip__remove')).toBeNull();
  });
});
