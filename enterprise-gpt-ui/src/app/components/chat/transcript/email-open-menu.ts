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
 * The primary control is an anchor, not a button: `mailto:` is a real navigation, so
 * middle-click and "copy link address" work, which a scripted `window.open` loses.
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
   * Names the recipient, which the compact form does not show, spelled as the menu label does.
   * Null without one, so the visible text stays the name.
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
   * Mail clients truncate a long `mailto:` body rather than refusing it, so the navigation
   * proceeds and the warning is raised even when the copy fails.
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
