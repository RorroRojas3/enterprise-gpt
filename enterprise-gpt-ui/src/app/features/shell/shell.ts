import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  linkedSignal,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterOutlet } from '@angular/router';
import { ShellChrome } from '@core/ui/shell-chrome';
import { UiStore } from '@core/ui/ui-store';
import { Icon } from '@shared/icon/icon';
import { MOBILE_VIEWPORT, TABLET_VIEWPORT } from '@shared/layout/breakpoints';
import { injectMediaQuery } from '@shared/layout/media-query';
import { Offcanvas } from '@shared/overlay/offcanvas/offcanvas';
import { DeleteConversationDialog } from './conversation-actions/delete-conversation-dialog';
import { RenameConversationDialog } from './conversation-actions/rename-conversation-dialog';
import { MobileNavbar } from './mobile-navbar/mobile-navbar';
import { DeleteProjectDialog } from './project-actions/delete-project-dialog';
import { ProjectFormDialog } from './project-actions/project-form-dialog';
import { Sidebar, SidebarMode } from './sidebar/sidebar';

/** Which of frames `3e`, `3f` and `3g` the viewport is in. */
type ShellMode = 'desktop' | 'tablet' | 'mobile';

/**
 * The signed-in application frame: the sidebar, and the routed screen beside it.
 *
 * A layout route rather than markup in `App`, because the four unguarded routes —
 * `/auth`, `/login-failed`, `/session-error`, `/signed-out` — and the forbidden page of
 * frame `6e` are all deliberately chrome-free, and a shell in `App` would put a sidebar
 * around every one of them.
 *
 * Since US-1403 it also owns the three viewport modes, and all sidebar policy with them.
 * `Sidebar` is now a controlled component: the same chevron means "persist a width" at
 * desktop and "open a transient overlay" at tablet, and only this component can see
 * which.
 */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DeleteConversationDialog,
    DeleteProjectDialog,
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
  // `shell-chrome.ts`, so it rides this lazy chunk. See the note in that file.
  providers: [ShellChrome],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
  host: {
    '[class.shell--collapsed]': "sidebarMode() === 'strip'",
    '[class.shell--animated]': 'animated()',
    '[class.shell--mobile]': "mode() === 'mobile'",
    '[class.shell--overlay]': 'tabletOverlayOpen()',
    '(keydown.escape)': 'onEscape()',
  },
})
export class Shell {
  private readonly _ui = inject(UiStore);
  private readonly _injector = inject(Injector);
  private readonly _animated = signal(false);
  private readonly _main = viewChild.required<ElementRef<HTMLElement>>('main');
  // Asks the sidebar to focus its own control rather than reaching in for one:
  // `.strip__button` is shared by the chevron, New conversation, search and every nav
  // link, so a document-order query here would silently follow a template reorder.
  private readonly _sidebar = viewChild(Sidebar);

  private readonly _isMobile = injectMediaQuery(MOBILE_VIEWPORT);
  private readonly _isTablet = injectMediaQuery(TABLET_VIEWPORT);

  /**
   * Both queries false means **desktop**, and that ordering is load-bearing: the test
   * fake answers `false` for every query a spec has not set, so "nothing configured" has
   * to resolve to the mode the existing suite was written against.
   */
  protected readonly mode = computed<ShellMode>(() =>
    this._isMobile() ? 'mobile' : this._isTablet() ? 'tablet' : 'desktop',
  );

  /**
   * What the sidebar renders. The **persisted preference is read at desktop only** — at
   * tablet the strip is the viewport's default rather than the user's choice, which is
   * frame `3g`'s own wording ("Defaults to the 60px strip"), and at mobile the sidebar
   * exists only inside the drawer.
   */
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
   * The transient overlay — frame `3g`'s expanded panel and frame `3e`'s drawer.
   *
   * On the shell rather than in `UiStore`, and the distinction is the point: every write
   * to that store goes to `localStorage`, and this is a gesture rather than a preference.
   * Keeping it here also keeps it off the initial bundle graph, which the root-scoped
   * store is on.
   *
   * **`linkedSignal` rather than a plain signal reset from an `effect`.** A rotation or a
   * resize is not a second gesture, and carrying the overlay across a breakpoint would
   * also change what it *is* — at mobile it is a modal dialog whose focus returns to a
   * hamburger that does not exist at tablet. Resetting it from an effect would work, but
   * the reset would land one pass *after* `mode()` changed, and `tabletOverlayOpen()`
   * reads both synchronously: for that window a drawer open at mobile would read as a
   * tablet overlay and `.shell__main` would take `inert`. Deriving it makes the reset
   * atomic with the mode read, so no ordering has to hold.
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
   * Moves focus into the screen without letting the fragment reach the router.
   *
   * **The overlay is dismissed first, and it has to be.** The skip link is a sibling of
   * `<main>` rather than a descendant, so it is not inerted with it — and it is the first
   * tab stop in the document. While the tablet overlay is up `<main>` carries `inert`, an
   * inert element is not focusable, and `preventDefault()` has already suppressed the
   * native `href`: the control would do nothing at all, silently. Dismissing is also the
   * honest reading of the gesture — the user has asked to go to the content.
   */
  protected skipToMain(event: Event): void {
    event.preventDefault();

    if (!this.tabletOverlayOpen()) {
      this._main().nativeElement.focus();
      return;
    }

    // Dismissing is not enough on its own, and this is the subtle half: writing the
    // signal only *schedules* change detection, so `inert` is still on `<main>` for the
    // rest of this task — and `focus()` on an inert element does nothing at all, without
    // error. The focus therefore waits for the render that removes the attribute.
    //
    // Not `dismissOverlay()`: that queues focus back onto the strip, which would take it
    // straight off the element this method exists to focus.
    this._overlayOpen.set(false);
    afterNextRender(() => this._main().nativeElement.focus(), { injector: this._injector });
  }

  /**
   * Either chevron. What it means depends on the width, which is the whole reason
   * `Sidebar` stopped deciding it.
   */
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

  /** Frame `3g`: tapping the backdrop collapses the panel back to the strip. */
  protected dismissOverlay(): void {
    this._overlayOpen.set(false);
    this._restoreFocusToStrip();
  }

  /**
   * Escape closes the tablet overlay, and only the tablet one. The mobile drawer is a
   * native dialog, whose `cancel` event `Offcanvas` already answers; handling the key
   * here too would be one close too many.
   */
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

  /**
   * Once the overlay collapses, the expanded panel's chevron no longer exists. Focus
   * follows to the strip control that replaced it, so dismissing from the backdrop or
   * with Escape does not leave the user at the document body, where the next Tab
   * restarts at the top of the page.
   */
  private _restoreFocusToStrip(): void {
    afterNextRender(() => this._sidebar()?.focusCollapseControl(), {
      injector: this._injector,
    });
  }

  constructor() {
    // The width transition is withheld until after the first render. `UiStore` restores
    // the collapsed preference before anything paints, so without this a returning user
    // would watch their sidebar animate from 260px to 60px on every load — an animation
    // of a state change that never happened.
    afterNextRender(() => this._animated.set(true));
  }
}
