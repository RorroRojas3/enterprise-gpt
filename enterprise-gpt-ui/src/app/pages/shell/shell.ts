import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { MOBILE_VIEWPORT, TABLET_VIEWPORT } from '@shared/utils/layout/breakpoints';
import { injectMediaQuery } from '@core/ui/media-query';
import { ShellChrome } from '@core/ui/shell-chrome-service';
import { UiStore } from '@state/ui-store';
import { Icon } from '@shared/components/icon/icon';
import { Offcanvas } from '@shared/components/offcanvas/offcanvas';
import { DeleteConversationDialog } from '@components/shell/conversation-actions/delete-conversation-dialog';
import { RenameConversationDialog } from '@components/shell/conversation-actions/rename-conversation-dialog';
import { McpCredentialDialog } from '@components/shell/mcp-credential-dialog';
import { MobileNavbar } from '@components/shell/mobile-navbar';
import { DeleteProjectDialog } from '@components/shell/project-actions/delete-project-dialog';
import { ProjectFormDialog } from '@components/shell/project-actions/project-form-dialog';
import { Sidebar, SidebarMode } from '@components/shell/sidebar/sidebar';

type ShellMode = 'desktop' | 'tablet' | 'mobile';

/**
 * A layout route, not markup in `App`, so the unguarded and forbidden pages stay chrome-free. It
 * owns sidebar policy, since only it knows whether a chevron persists a width or opens an overlay.
 */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DeleteConversationDialog,
    DeleteProjectDialog,
    McpCredentialDialog,
    Icon,
    MobileNavbar,
    NgTemplateOutlet,
    Offcanvas,
    ProjectFormDialog,
    RenameConversationDialog,
    RouterOutlet,
    Sidebar,
  ],
  // Provided here rather than at the root: nothing eagerly reachable imports
  // `shell-chrome-service.ts`, so it rides this lazy chunk. See the note in that file.
  providers: [ShellChrome],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
  host: {
    class: 'd-flex flex-column flex-md-row position-relative overflow-hidden',
    '[class.shell--collapsed]': "sidebarMode() === 'strip'",
    '[class.shell--animated]': 'animated()',
    '[class.shell--overlay]': 'tabletOverlayOpen()',
    '(keydown.escape)': 'onEscape()',
  },
})
export class Shell {
  private readonly _ui = inject(UiStore);
  private readonly _injector = inject(Injector);
  private readonly _animated = signal(false);
  private readonly _main = viewChild.required<ElementRef<HTMLElement>>('main');
  // Asks the sidebar to focus its own control: the strip's buttons share their classes, so a
  // document-order query here would silently follow a template reorder.
  private readonly _sidebar = viewChild(Sidebar);

  private readonly _isMobile = injectMediaQuery(MOBILE_VIEWPORT);
  private readonly _isTablet = injectMediaQuery(TABLET_VIEWPORT);

  /** Both queries false means desktop: the test fake answers false for any unset query. */
  protected readonly mode = computed<ShellMode>(() =>
    this._isMobile() ? 'mobile' : this._isTablet() ? 'tablet' : 'desktop',
  );

  /** The persisted preference applies at desktop only; tablet defaults to the strip. */
  protected readonly sidebarMode = computed<SidebarMode>(() => {
    switch (this.mode()) {
      case 'desktop':
        return this._ui.sidebarCollapsed() ? 'strip' : 'expanded';
      case 'tablet':
        return this._overlayOpen() ? 'expanded' : 'strip';
      case 'mobile':
        return 'overlay';
    }
  });

  /**
   * The transient overlay: a gesture, not a preference, so not in `UiStore` (which persists).
   * Linked to the mode so a breakpoint change resets it atomically; an effect would leave a
   * pass where a mobile drawer reads as a tablet overlay and inerts `<main>`.
   */
  private readonly _overlayOpen = linkedSignal<ShellMode, boolean>({
    source: this.mode,
    computation: () => false,
  });

  protected readonly animated = this._animated.asReadonly();
  protected readonly tabletOverlayOpen = computed(
    () => this.mode() === 'tablet' && this._overlayOpen(),
  );
  protected readonly mobileDrawerOpen = computed(
    () => this.mode() === 'mobile' && this._overlayOpen(),
  );

  /**
   * Focuses `<main>` without letting the fragment reach the router. Dismisses the tablet
   * overlay first: `<main>` is inert under it, so the skip link would otherwise do nothing.
   */
  protected skipToMain(event: Event): void {
    event.preventDefault();

    if (!this.tabletOverlayOpen()) {
      this._main().nativeElement.focus();
      return;
    }

    // `inert` stays until the next render, and focusing an inert element silently fails. Not
    // `dismissOverlay()`, which would queue focus back onto the strip.
    this._overlayOpen.set(false);
    afterNextRender(() => this._main().nativeElement.focus(), { injector: this._injector });
  }

  /** Either chevron; what it means depends on the width. */
  protected onSidebarToggled(): void {
    if (this.mode() === 'desktop') {
      this._ui.toggleSidebar();
      return;
    }
    this._overlayOpen.update((open) => !open);
  }

  /** The strip's search button: expand, by whichever mechanism this width expands by. */
  protected onSearchRequested(): void {
    if (this.mode() === 'desktop') {
      this._ui.setSidebarCollapsed(false);
      return;
    }
    this._overlayOpen.set(true);
  }

  protected openOverlay(): void {
    this._overlayOpen.set(true);
  }

  /** Pressing the backdrop collapses the panel back to the strip. */
  protected dismissOverlay(): void {
    this._overlayOpen.set(false);
    this._restoreFocusToStrip();
  }

  /** Tablet only: `Offcanvas` already answers the mobile drawer's native `cancel`. */
  protected onEscape(): void {
    if (this.tabletOverlayOpen()) {
      this.dismissOverlay();
    }
  }

  protected onDrawerOpenChange(open: boolean): void {
    if (!open) {
      this._overlayOpen.set(false);
    }
  }

  /** The panel's chevron is gone once it collapses, so focus moves to the strip's replacement. */
  private _restoreFocusToStrip(): void {
    afterNextRender(() => this._sidebar()?.focusCollapseControl(), {
      injector: this._injector,
    });
  }

  constructor() {
    // No width transition until after the first render, or a restored collapsed preference
    // would animate from 260px to 60px on every load.
    afterNextRender(() => this._animated.set(true));
  }
}
