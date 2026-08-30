import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { EmailDraft } from '@domain/email/email-draft';
import { EmailCard } from './email-card';

const draft = (over: Partial<EmailDraft> = {}): EmailDraft => ({
  to: [],
  cc: [],
  subject: null,
  body: 'Hi Alice,\n\nThanks.',
  ...over,
});

describe('EmailCard', () => {
  let fixture: ComponentFixture<EmailCard>;

  async function render(value: EmailDraft): Promise<HTMLElement> {
    fixture = TestBed.createComponent(EmailCard);
    fixture.componentRef.setInput('draft', value);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('shows the recipient, so it is reviewable before the mail client opens', async () => {
    const host = await render(draft({ to: ['alice@contoso.com'] }));

    expect(host.textContent).toContain('alice@contoso.com');
  });

  it('lists several recipients together', async () => {
    const host = await render(draft({ to: ['a@b.com', 'c@d.com'] }));

    expect(host.textContent).toContain('a@b.com, c@d.com');
  });

  it('renders the subject when there is one', async () => {
    const host = await render(draft({ subject: 'Q3 budget review' }));

    expect(host.textContent).toContain('Q3 budget review');
  });

  it('omits a field row entirely rather than showing an empty one', async () => {
    const host = await render(draft());
    const terms = [...host.querySelectorAll('dt')].map((dt) => dt.textContent?.trim());

    expect(terms).toEqual([]);
  });

  it('renders the body as text, never as markup', async () => {
    const host = await render(draft({ body: '<img src=x onerror=alert(1)>' }));

    expect(host.querySelector('img')).toBeNull();
    expect(host.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('offers the open control', async () => {
    const host = await render(draft({ to: ['alice@contoso.com'], subject: 'Hi' }));
    const open = host.querySelector('a[href^="mailto:"]');

    expect(open?.getAttribute('href')).toBe(
      'mailto:alice@contoso.com?subject=Hi&body=Hi%20Alice%2C%0D%0A%0D%0AThanks.',
    );
  });
});
