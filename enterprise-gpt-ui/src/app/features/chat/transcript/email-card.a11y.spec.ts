import { ComponentFixture, TestBed } from '@angular/core/testing';
import { getByRole } from '@testing-library/dom';
import { afterEach, describe, expect, it } from 'vitest';
import { EmailDraft } from '@shared/utils/email/email-draft';
import { THEMES, applyTheme, clearTheme, expectNoSeriousViolations } from '@testing/a11y';
import { EmailCard } from './email-card';

/**
 * Mounted directly, since no replayed route fixture is an email. Audited with the menu open so
 * the `aria-controls` target and the rows' contrast are covered.
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
      const trigger = getByRole(host, 'button', { name: /^Other ways to email/ });

      trigger.click();
      await fixture.whenStable();

      expect(trigger.getAttribute('aria-expanded')).toBe('true');

      await expectNoSeriousViolations(host, `EmailCard (${theme})`);
    });
  }
});
