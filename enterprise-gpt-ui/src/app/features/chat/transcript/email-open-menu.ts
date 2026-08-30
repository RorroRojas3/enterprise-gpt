import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { copyText } from '@core/clipboard/copy-text';
import { ToastStore } from '@core/notifications/toast-store';
import {
  buildMailtoUrl,
  buildOutlookWebComposeUrl,
  exceedsMailtoLimit,
  formatEmailForClipboard,
} from '@domain/email/compose-url';
import { EmailDraft } from '@domain/email/email-draft';
import { BrandIcon } from '@shared/icon/brand-icon';
import { Icon } from '@shared/icon/icon';
import { Menu } from '@shared/overlay/menu/menu';
import { MenuItem } from '@shared/overlay/menu/menu-item';

/**
 * Hands a composed email to a mail client. The primary control is an anchor
 * rather than a button because `mailto:` is a real navigation: middle-click,
 * "copy link address" and the browser's handler prompt all come free, and none
 * survive a scripted `window.open`.
 */
@Component({
  selector: 'app-email-open-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandIcon, Icon, Menu, MenuItem],
  templateUrl: './email-open-menu.html',
  styleUrl: './email-open-menu.scss',
})
export class EmailOpenMenu {
  readonly draft = input.required<EmailDraft>();
  /** Bare footer styling rather than the card header's outlined button. */
  readonly compact = input<boolean>(false);

  private readonly toasts = inject(ToastStore);

  protected readonly menuOpen = signal(false);

  protected readonly mailtoUrl = computed(() => buildMailtoUrl(this.draft()));
  protected readonly outlookWebUrl = computed(() => buildOutlookWebComposeUrl(this.draft()));

  private readonly recipients = computed(() => this.draft().to.join(', '));

  /**
   * Both controls name the recipient, and both spell it the same way: it is the
   * part of a generated draft a reader most needs before acting on it, and the
   * compact form shows no address of its own. Null when there is no recipient to
   * name, so the anchor keeps its visible text as its name rather than restating
   * it.
   */
  protected readonly openLabel = computed(() => {
    const to = this.recipients();
    return to === '' ? null : `Open email to ${to}`;
  });

  protected readonly menuLabel = computed(() => {
    const to = this.recipients();
    return to === '' ? 'Other ways to send this email' : `Other ways to email ${to}`;
  });

  /**
   * `mailto:` has no length limit of its own, but the receiving client does, and
   * Outlook truncates a long body rather than refusing it. The navigation is
   * deliberately left to proceed — a `mailto:` does not unload the page — and the
   * warning is raised whether or not the clipboard accepted the copy, because a
   * refused clipboard is exactly when the reader most needs telling.
   */
  protected async onOpenDefault(): Promise<void> {
    if (!exceedsMailtoLimit(this.mailtoUrl())) {
      return;
    }

    const copied = await this.copy();

    this.toasts.warning(
      'This email may be cut short',
      copied
        ? 'Your mail app limits how much it accepts. The full email is on your clipboard — paste if anything is missing.'
        : 'Your mail app limits how much it accepts, and the email could not be copied. Use Copy email to get the rest.',
    );
  }

  protected async onCopy(): Promise<void> {
    if (await this.copy()) {
      this.toasts.success('Email copied');
    }
  }

  private copy(): Promise<boolean> {
    return copyText(formatEmailForClipboard(this.draft()));
  }
}
