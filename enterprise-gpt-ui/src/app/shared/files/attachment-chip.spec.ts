import { TestBed } from '@angular/core/testing';
import { getByRole, getByText, queryByRole } from '@testing-library/dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Attachment, AttachmentState } from '@shared/models/attachment.model';
import { fileGlyph } from '@shared/utils/documents/file-glyph';
import { iconOf } from '@testing/overlays';
import { AttachmentChip } from './attachment-chip';

function attachment(state: AttachmentState): Attachment {
  return { id: 'a1', fileName: 'q3-report.pdf', sizeLabel: '2.4 MB', state };
}

describe('fileGlyph', () => {
  it.each([
    ['report.pdf', 'bi-file-earmark-pdf', 'danger'],
    ['sheet.XLSX', 'bi-file-earmark-excel', 'success'],
    ['notes.md', 'bi-filetype-md', 'info'],
    ['data.csv', 'bi-filetype-csv', 'info'],
    ['deck.pptx', 'bi-file-earmark-ppt', 'warning'],
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

  async function render(state: AttachmentState, inputs: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(AttachmentChip);
    fixture.componentRef.setInput('attachment', attachment(state));
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    await fixture.whenStable();
    return { fixture, host: fixture.nativeElement as HTMLElement };
  }

  it('shows uploading as measurable progress, not just a spinner', async () => {
    const { host } = await render({ kind: 'uploading', percent: 42 });

    const bar = getByRole(host, 'progressbar', { name: 'Uploading q3-report.pdf' });
    expect(bar.getAttribute('aria-valuenow')).toBe('42');
    expect(host.textContent).toContain('42%');
  });

  it('shows processing with its sub-status in a polite live region', async () => {
    const { host } = await render({
      kind: 'processing',
      subStatus: 'Generating embeddings… 3 of 12 pages',
    });

    expect(getByRole(host, 'status').textContent).toContain('3 of 12 pages');
    expect(host.querySelector('.spinner-border')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('shows ready with its size', async () => {
    const { host } = await render({ kind: 'ready' });

    expect(host.textContent).toContain('2.4 MB');
    expect(host.textContent).toContain('Ready to use');
    expect(iconOf(host)).toBe('bi-check-circle-fill');
    expect(queryByRole(host, 'progressbar')).toBeNull();
  });

  it('shows a failed job with its reason and a named retry', async () => {
    const { fixture, host } = await render({
      kind: 'failed',
      reason: 'The document could not be read. It may be corrupt or password-protected.',
    });
    const retried = vi.fn();
    fixture.componentInstance.retried.subscribe(retried);

    expect(host.textContent).toContain('could not be read');
    expect(iconOf(host)).toBe('bi-x-circle-fill');

    getByRole(host, 'button', { name: 'Retry uploading q3-report.pdf' }).click();
    expect(retried).toHaveBeenCalledWith('a1');
  });

  it('names the refused extension and the supported set, and offers no retry', async () => {
    // The same file would be refused again, so a Retry here could not work; see `canRetry`.
    const { host } = await render({
      kind: 'unsupported',
      reason: '.mov isn’t supported — PDF, DOCX, MD, PPTX, TXT',
    });

    expect(host.textContent).toContain('isn’t supported');
    expect(host.textContent).toContain('PPTX');
    expect(queryByRole(host, 'button', { name: /^Retry/ })).toBeNull();
  });

  it('renders expired-or-unknown differently from failed, and offers no retry', async () => {
    const { host } = await render({ kind: 'unknown' });

    expect(host.textContent).toContain('Status is only kept for a limited time.');
    expect(host.textContent?.toLowerCase()).not.toContain('failed');
    expect(iconOf(host)).toBe('bi-question-circle');
    expect(queryByRole(host, 'button', { name: /^Retry/ })).toBeNull();
  });

  it('gives the remove control a name that says what it removes', async () => {
    const { host } = await render({ kind: 'ready' });

    expect(getByRole(host, 'button', { name: 'Remove q3-report.pdf' })).not.toBeNull();
  });

  it('says "dismiss" once there is nothing left to cancel', async () => {
    // No API detaches a conversation document, so the control only hides the chip
    // from here on. Calling it "Remove" would claim an effect it does not have.
    const { host } = await render({ kind: 'ready' }, { removeAction: 'dismiss' });

    expect(getByRole(host, 'button', { name: 'Dismiss q3-report.pdf' })).not.toBeNull();
    expect(queryByRole(host, 'button', { name: /^Remove/ })).toBeNull();
  });

  it('offers download only where the caller says the file can be fetched back', async () => {
    const plain = await render({ kind: 'ready' });
    expect(queryByRole(plain.host, 'button', { name: /^Download/ })).toBeNull();

    const { fixture, host } = await render({ kind: 'ready' }, { downloadable: true });
    const downloaded = vi.fn();
    fixture.componentInstance.downloaded.subscribe(downloaded);

    getByRole(host, 'button', { name: 'Download q3-report.pdf' }).click();

    expect(downloaded).toHaveBeenCalledWith('a1');
  });

  it('keeps an in-flight download focusable but inert, and says why', async () => {
    const { fixture, host } = await render(
      { kind: 'ready' },
      { downloadable: true, downloading: true },
    );
    const downloaded = vi.fn();
    fixture.componentInstance.downloaded.subscribe(downloaded);

    const button = getByRole<HTMLButtonElement>(host, 'button', {
      name: 'Downloading q3-report.pdf',
    });
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.getAttribute('aria-busy')).toBe('true');

    button.click();
    expect(downloaded).not.toHaveBeenCalled();
  });

  // Both arms, because only one of them is obviously wrong when it breaks: announcing every uploaded
  // file as one the assistant made would read as correct on the surface that produced this label.
  it.each([
    [false, 'Download q3-report.pdf'],
    [true, 'Download generated file q3-report.pdf'],
  ])('names the download for a generated=%s file as "%s"', async (generated, label) => {
    const { host } = await render({ kind: 'ready' }, { downloadable: true, generated });

    expect(getByRole(host, 'button', { name: label })).not.toBeNull();
  });

  describe('a file the assistant produced', () => {
    async function renderGenerated() {
      return (await render({ kind: 'ready' }, { generated: true })).host;
    }

    it('leads with the file type rather than the upload tick', async () => {
      // The tick answers "your upload finished", which is not a question anyone asked of a
      // file they never uploaded.
      const host = await renderGenerated();

      expect(iconOf(host)).toBe('bi-file-earmark-pdf');
    });

    it('says so in text, not by colour or glyph alone', async () => {
      const host = await renderGenerated();

      const group = getByRole(host, 'group', { name: 'Generated file, q3-report.pdf' });
      expect(getByText(group, 'Generated')).not.toBeNull();
    });

    it('reports the size instead of the upload lifecycle', async () => {
      const host = await renderGenerated();

      expect(host.textContent).toContain('2.4 MB');
      expect(host.textContent).not.toContain('Ready to use');
    });
  });

  it('hides the remove control where the caller forbids it', async () => {
    const { host } = await render({ kind: 'ready' }, { removable: false });

    expect(queryByRole(host, 'button', { name: /^(Remove|Dismiss)/ })).toBeNull();
  });
});
