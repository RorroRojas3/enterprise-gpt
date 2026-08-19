import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ShellChrome } from '@core/ui/shell-chrome';
import { BrandLogo } from '@shared/brand-logo/brand-logo';
import { Icon } from '@shared/icon/icon';

/**
 * Frame `1d`'s 54px bar, below 768px — the only chrome above the routed screen at that
 * width, and the one place the sidebar can be reached from.
 *
 * Its title and its trailing controls come from {@link ShellChrome}, which the routed
 * screen writes: frame `1d` puts the product name there with the conversation kebab
 * beside it, frame `5m` puts "Admin" there with nothing. The hamburger is the shell's,
 * because the sidebar is.
 */
@Component({
  selector: 'app-mobile-navbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrandLogo, Icon, NgTemplateOutlet],
  templateUrl: './mobile-navbar.html',
  styleUrl: './mobile-navbar.scss',
})
export class MobileNavbar {
  private readonly _chrome = inject(ShellChrome);

  readonly menuRequested = output<void>();

  protected readonly chrome = this._chrome.state;
}
