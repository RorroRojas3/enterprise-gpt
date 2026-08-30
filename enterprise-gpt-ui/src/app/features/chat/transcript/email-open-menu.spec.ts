import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ToastStore } from '@core/notifications/toast-store';
import { MAILTO_SAFE_LENGTH } from '@domain/email/compose-url';
import { EmailDraft } from '@domain/email/email-draft';
import { EmailOpenMenu } from './email-open-menu';

const draft = (over: Partial<EmailDraft> = {}): EmailDraft => ({
  to: [],
  cc: [],
  subject: null,
  body: 'Short body.',
  ...over,
});

describe('EmailOpenMenu', () => {
  let fixture: ComponentFixture<EmailOpenMenu>;
  let host: HTMLElement;
  let writeText: ReturnType<typeof vi.fn>;
  let clipboardDescriptor: PropertyDescriptor | undefined;

  async function render(value: EmailDraft): Promise<void> {
    fixture = TestBed.createComponent(EmailOpenMenu);
    fixture.componentRef.setInput('draft', value);
    host = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  /** The click, plus the microtasks the clipboard write and the toast settle on. */
  async function pressOpen(): Promise<void> {
    openLink().dispatchEvent(new MouseEvent('click'));
    await fixture.whenStable();
    await Promise.resolve();
  }

  function openLink(): HTMLAnchorElement {
    return host.querySelector('a[href^="mailto:"]') as HTMLAnchorElement;
  }

  beforeEach(() => {
    writeText = vi.fn().mockResolvedValue(undefined);
    clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    if (clipboardDescriptor !== undefined) {
      Object.defineProperty(navigator, 'clipboard', clipboardDescriptor);
    } else {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it('opens the default client through a real mailto anchor', async () => {
    await render(draft({ to: ['alice@contoso.com'], subject: 'Q3' }));

    expect(openLink().getAttribute('href')).toBe(
      'mailto:alice@contoso.com?subject=Q3&body=Short%20body.',
    );
  });

  it('leaves the navigation alone for a body the client can carry', async () => {
    await render(draft({ subject: 'Q3' }));
    openLink().dispatchEvent(new MouseEvent('click'));

    expect(writeText).not.toHaveBeenCalled();
  });

  it('copies the whole email when the url is long enough to be truncated', async () => {
    const body = 'a'.repeat(MAILTO_SAFE_LENGTH + 100);
    await render(draft({ to: ['alice@contoso.com'], subject: 'Q3', body }));

    await pressOpen();

    expect(writeText).toHaveBeenCalledWith(`To: alice@contoso.com\nSubject: Q3\n\n${body}`);
    expect(TestBed.inject(ToastStore).toasts()).toHaveLength(1);
  });

  it('still warns about truncation when the clipboard refuses', async () => {
    // The copy is the mitigation, not the message. A refused clipboard is exactly
    // when the reader most needs telling the draft may arrive incomplete.
    writeText.mockRejectedValue(new Error('denied'));
    await render(draft({ subject: 'Q3', body: 'a'.repeat(MAILTO_SAFE_LENGTH + 100) }));

    await pressOpen();

    const toast = TestBed.inject(ToastStore).toasts()[0];
    expect(toast?.tone).toBe('warning');
    expect(toast?.detail).toContain('could not be copied');
  });

  it('raises nothing at all for a body the client can carry', async () => {
    await render(draft({ subject: 'Q3' }));

    await pressOpen();

    expect(TestBed.inject(ToastStore).toasts()).toHaveLength(0);
  });

  it('does not cancel the navigation it just copied for', async () => {
    await render(draft({ body: 'a'.repeat(MAILTO_SAFE_LENGTH + 100) }));
    const event = new MouseEvent('click', { cancelable: true });
    openLink().dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });

  it('names the open control after the recipient, spelled as the draft spells it', async () => {
    await render(draft({ to: ['Alice.Smith@Contoso.com'] }));

    // Both controls, one spelling: they name the same person, and a lowercased
    // copy on one of them would announce two addresses for one recipient.
    expect(openLink().getAttribute('aria-label')).toBe('Open email to Alice.Smith@Contoso.com');
    expect(host.querySelector('.menu__trigger')?.textContent).toContain('Alice.Smith@Contoso.com');
  });

  it('leaves the open control unlabelled when there is no recipient to name', async () => {
    await render(draft());

    expect(openLink().getAttribute('aria-label')).toBeNull();
  });

  it('names the menu after the recipient, so the trigger says who it will mail', async () => {
    await render(draft({ to: ['alice@contoso.com'] }));

    // The name is the trigger's own visually-hidden text, which is where `Menu`
    // puts it whenever a consumer does not project a trigger face of its own.
    expect(host.querySelector('.menu__trigger')?.textContent).toContain('alice@contoso.com');
  });

  it('offers an Outlook web link that opens in its own tab', async () => {
    await render(draft({ to: ['alice@contoso.com'], subject: 'Q3' }));
    (host.querySelector('.menu__trigger') as HTMLButtonElement).click();
    await fixture.whenStable();

    const web = host.querySelector<HTMLAnchorElement>('a[href*="outlook.office.com"]');
    expect(web?.getAttribute('href')).toContain('to=alice%40contoso.com');
    expect(web?.getAttribute('rel')).toBe('noopener');
  });
});
