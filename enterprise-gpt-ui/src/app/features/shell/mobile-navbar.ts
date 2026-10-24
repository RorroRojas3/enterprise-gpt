import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { ShellChrome } from '@core/layout/shell-chrome';
import { BrandLogo } from '@shared/brand/brand-logo';
import { Icon } from '@shared/icons/icon';

/**
 * The only chrome above the routed screen below 768px. Its title and trailing controls come
 * from {@link ShellChrome}, which the screen writes; the hamburger is the shell's.
 */
@Component({
  selector: 'app-mobile-navbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLogo, Icon, NgTemplateOutlet],
  templateUrl: './mobile-navbar.html',
  styleUrl: './mobile-navbar.scss',
  host: { class: 'd-flex flex-shrink-0 align-items-center gap-3 border-bottom bg-surface' },
})
export class MobileNavbar {
  private readonly _chrome = inject(ShellChrome);

  readonly menuRequested = output<void>();

  protected readonly chrome = this._chrome.state;
}
