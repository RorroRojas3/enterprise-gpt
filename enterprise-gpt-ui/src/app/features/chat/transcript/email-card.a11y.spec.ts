import { ComponentFixture, TestBed } from '@angular/core/testing';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { EmailDraft } from '@domain/email/email-draft';
import { afterEach, describe, expect, it } from 'vitest';
import { EmailCard } from './email-card';

/**
 * The email card, which no route-level audit can reach: a `/chat/:id` audit
 * replays a transcript, and none of the fixtures it replays is an email.
 *
 * Audited with the menu open, because the panel is where the trigger's
 * `aria-controls` target lives and a closed one audits neither that relationship
 * nor the rows' own contrast.
 */
describe('EmailCard accessibility', () => {
  let fixture: ComponentFixture<EmailCard> | null = null;

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    clearTheme();
  });

  const DRAFT: EmailDraft = {
    to: ['alice@contoso.com'],
    cc: ['bob@contoso.com'],
    subject: 'Moving Thursday’s budget review',
    body: 'Hi Alice,\n\nCould we move Thursday to Friday morning?\n\nBest regards,\nRodrigo',
  };

  for (const theme of THEMES) {
    it(`has no serious or critical violations in the ${theme} theme`, async () => {
      applyTheme(theme);

      TestBed.configureTestingModule({});
      fixture = TestBed.createComponent(EmailCard);
      fixture.componentRef.setInput('draft', DRAFT);

      // Attached, because axe reads computed style and a detached element has none.
      document.body.append(fixture.nativeElement as HTMLElement);
      await fixture.whenStable();

      const host = fixture.nativeElement as HTMLElement;
      const trigger = host.querySelector<HTMLButtonElement>('.menu__trigger');

      expect(trigger).not.toBeNull();
      trigger!.click();
      await fixture.whenStable();

      expect(trigger!.getAttribute('aria-expanded')).toBe('true');

      await expectNoSeriousViolations(host, `EmailCard (${theme})`);
    });
  }
});
