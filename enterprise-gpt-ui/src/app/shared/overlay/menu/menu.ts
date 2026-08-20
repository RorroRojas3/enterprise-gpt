import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  afterEveryRender,
  afterRenderEffect,
  computed,
  contentChild,
  effect,
  inject,
  input,
  model,
  signal,
  viewChild,
} from '@angular/core';
import { onDismiss } from '@shared/a11y/dismiss';
import { Icon } from '@shared/icon/icon';
import { IconName } from '@shared/icon/icon-names';
import { Tooltip } from '../tooltip/tooltip';
import { AnchoredPosition, anchoredPosition } from '../anchored-panel';
import { MenuTriggerContent } from './menu-trigger-content';

let nextId = 0;

/**
 * The kebab menu — every row's actions, the conversation header, the admin tables.
 *
 * Hand-rolled rather than Bootstrap's dropdown, which costs 35.7 kB because it bundles
 * Popper. Popper exists to flip a menu around a viewport edge; every menu in this
 * design is anchored to its own row, so a `position: fixed` panel placed from the
 * trigger's rect does the same job. It also escapes the sidebar's `overflow-y: auto`,
 * which an absolutely-positioned panel would be clipped by.
 *
 * Items are projected, so the kit never learns about Rename or Delete:
 * ```html
 * <app-menu label="Actions for Helios release">
 *   <button appMenuItem (click)="rename()">Rename</button>
 *   <hr appMenuSeparator />
 *   <button appMenuItem class="menu__item--danger" (click)="remove()">Delete</button>
 * </app-menu>
 * ```
 */
@Component({
  selector: 'app-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon, Tooltip],
  templateUrl: './menu.html',
  styleUrl: './menu.scss',
})
export class Menu {
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly triggerRef = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  private readonly panelRef = viewChild<ElementRef<HTMLDivElement>>('panel');

  /**
   * The accessible name of the trigger. Name the subject: "Actions for Helios" —
   * and when the trigger projects its own face, include the current value
   * ("Model: GPT-5 Enterprise"), because the name is all a screen reader gets.
   */
  readonly label = input.required<string>();
  readonly icon = input<IconName>('bi-three-dots-vertical');
  readonly align = input<'start' | 'end'>('end');
  readonly open = model<boolean>(false);
  /**
   * Keeps the panel open when an item is activated — the multi-select shape
   * (frame `2c`), where each toggle applies immediately and closing would force
   * a reopen per choice. Escape, Tab, outside-pointerdown, and scroll still
   * dismiss: the panel stays open *while toggling*, it is not modal.
   */
  readonly stayOpen = input<boolean>(false);
  /**
   * `aria-disabled` + inert, never the native attribute — a natively disabled
   * button cannot take focus, which would strand keyboard users with no
   * element to read the disabled state from.
   */
  readonly disabled = input<boolean>(false);
  /**
   * A tooltip on the trigger, for when a disabled menu owes the reader a reason
   * (US-1502: "Available when the response finishes").
   *
   * On the trigger and not on the host: the directive names an unnamed host with
   * `aria-label`, and `<app-menu>` carries no role, which axe reports as
   * `aria-prohibited-attr` — a serious violation, so the accessibility gate would
   * fail the build on it. The trigger is a `<button>` that already has a name from
   * {@link label}, so the tooltip stays purely visual and the reason belongs in
   * `label` for assistive technology.
   */
  readonly hint = input<string | null>(null);
  readonly panelWidth = input<number>(204);
  /**
   * `up` opens above the trigger — the composer sits at the bottom of the
   * screen. Static rather than flipped at the viewport edge, which is exactly
   * why this panel never needed Popper.
   */
  readonly direction = input<'down' | 'up'>('down');

  private readonly triggerContent = contentChild(MenuTriggerContent);
  /** Whether a consumer projected its own trigger face. */
  protected readonly hasTriggerContent = computed(() => this.triggerContent() !== undefined);

  protected readonly panelId = `menu-panel-${nextId++}`;
  /** Placed by `anchoredPosition`, which US-307's project picker shares. */
  protected readonly position = signal<AnchoredPosition>({
    top: 0,
    bottom: null,
    left: 0,
    width: null,
  });

  /** Set when opening by keyboard, so focus lands on the right end of the list. */
  private focusLastOnOpen = false;
  /** Whether the panel was rendered on the previous pass; effects have no memory of their own. */
  private wasOpen = false;
  /**
   * Set by {@link close}, which has already decided where focus goes.
   *
   * Without it the fall-through rescue below would override `close(false)`'s deliberate
   * "do not steal focus" — an outside press on non-focusable space leaves focus on
   * `<body>`, which is indistinguishable from a fall-through by inspection alone.
   */
  private closeOwnsFocus = false;

  constructor() {
    // Dismissal is driven by the state, not by whichever method set it. `open` is a
    // two-way model, so a parent binding `[(open)]` would otherwise open a menu with
    // no outside-click or scroll handling, and close one without ever aborting the
    // document-level listeners.
    effect((onCleanup) => {
      if (!this.open()) {
        return;
      }

      const controller = new AbortController();
      onDismiss(
        // The component host, not the trigger. The panel is the trigger's *sibling*,
        // so a pointerdown on a menu item is not in the trigger's composedPath — the
        // menu would dismiss itself before the item's own click could fire, and every
        // consumer's action would silently do nothing.
        this.hostRef.nativeElement,
        {
          onOutside: () => this.close(false),
          // Closing rather than repositioning: a fixed panel that follows a scrolling
          // row is more distracting than one that gets out of the way.
          onScroll: () => this.close(false),
        },
        controller.signal,
      );

      onCleanup(() => controller.abort());
    });

    afterRenderEffect({
      // earlyRead, not read: the phases run earlyRead → write → mixedReadWrite → read,
      // so measuring in `read` would land after the write that needs the measurement.
      // Splitting them this way is what keeps the layout read out of the write pass.
      earlyRead: () => {
        if (!this.open() || !this.panelRef()) {
          return null;
        }
        // The viewport is read here too, and not in `write` where it used to be: both
        // are layout reads, and the phase split exists precisely to keep them out of
        // the write pass.
        // `documentElement.clientWidth` for the width, deliberately, and `innerWidth`
        // for nothing: a `position: fixed` panel's containing block **excludes** the
        // classic scrollbar, so sizing it from `innerWidth` overhangs by the scrollbar's
        // thickness and produces the horizontal scrollbar US-1403's last criterion
        // forbids — on exactly the desktop browser someone narrows to check it. It is
        // the JS spelling of the `100vw` mistake `offcanvas.scss` calls out one file
        // over.
        const view = this.document.defaultView;
        return {
          anchor: this.triggerRef().nativeElement.getBoundingClientRect(),
          viewport: {
            width: this.document.documentElement.clientWidth,
            height: view?.innerHeight ?? this.document.documentElement.clientHeight,
          },
        };
      },
      write: (measured) => {
        const box = measured();
        const panel = this.panelRef();
        if (!box || !panel) {
          return;
        }
        // `panelWidth()`, not the panel's measured `offsetWidth`: the measurement
        // reflects whatever width was bound on the previous pass, so the first open at
        // a narrow width would compute its inset from a stale number.
        this.position.set(
          anchoredPosition(box.anchor, this.panelWidth(), box.viewport, {
            direction: this.direction(),
            align: this.align(),
          }),
        );
        if (this.items().length === 0) {
          // No item to rove onto (a pending or empty picker state): focus the
          // panel itself so Escape and the status content stay reachable.
          panel.nativeElement.focus();
        } else {
          this.focusItem(this.focusLastOnOpen ? -1 : 0);
        }
        this.focusLastOnOpen = false;
      },
    });

    // A content swap while open can unmount the focused item — the retry flow
    // replaces the Retry button with a status row — dropping focus to <body>,
    // where every key the panel handles goes dead. While the panel is open,
    // focus belongs inside it, so a fall-through is corrected rather than a
    // user choice overridden: any deliberate way out (Tab, Escape, outside
    // press, activation) closes the menu first.
    //
    // The same rule applies on the way out, for one path only: a consumer clearing the
    // two-way `open` model never reaches `close()` — US-1502's download menu closes
    // itself when the browser takes the file — and the panel it destroys is holding the
    // focused item, so focus lands on <body> with nothing to catch it. Every close that
    // goes through `close()` has already decided about focus, including the deliberate
    // no-steal on an outside press, and is left alone.
    afterEveryRender({
      mixedReadWrite: () => {
        const active = this.document.activeElement;
        const fellThrough = active === null || active === this.document.body || !active.isConnected;

        if (!this.open()) {
          if (this.wasOpen && fellThrough && !this.closeOwnsFocus) {
            this.triggerRef().nativeElement.focus();
          }
          this.wasOpen = false;
          this.closeOwnsFocus = false;

          return;
        }

        this.wasOpen = true;
        const panel = this.panelRef()?.nativeElement;
        if (!panel || !fellThrough) {
          return;
        }
        (this.items()[0] ?? panel).focus();
      },
    });

    this.destroyRef.onDestroy(() => this.close(false));
  }

  /**
   * The trigger button, so a surface can anchor its own overlay where this menu was
   * (US-307: "Move to project" closes the kebab and opens the picker in its place).
   *
   * A method rather than a computed, to be called at the gesture: `triggerRef` is a
   * required `viewChild`, and a template binding would read it during the first change
   * detection pass, before the view exists.
   */
  triggerElement(): HTMLButtonElement {
    return this.triggerRef().nativeElement;
  }

  protected toggle(): void {
    if (this.disabled()) {
      return;
    }
    if (this.open()) {
      this.close();
    } else {
      this.openMenu();
    }
  }

  protected onTriggerKeydown(event: KeyboardEvent): void {
    if (this.disabled()) {
      return;
    }
    // Focus can legitimately sit on the trigger while the panel is open (a
    // panel with no items focuses itself, but never steals from the trigger),
    // so Escape must work from here too, not only inside the panel.
    if (event.key === 'Escape' && this.open()) {
      event.preventDefault();
      this.close();
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.focusLastOnOpen = event.key === 'ArrowUp';
      this.openMenu();
    }
  }

  protected onPanelKeydown(event: KeyboardEvent): void {
    const items = this.items();
    if (items.length === 0) {
      return;
    }

    const current = items.indexOf(this.document.activeElement as HTMLElement);

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.focusItem((current + 1) % items.length);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusItem((current - 1 + items.length) % items.length);
        break;
      case 'Home':
        event.preventDefault();
        this.focusItem(0);
        break;
      case 'End':
        event.preventDefault();
        this.focusItem(-1);
        break;
      case 'Tab':
        // Tab leaves the menu rather than cycling inside it: this is a menu button,
        // not a dialog, and the page behind it is still live.
        this.close();
        break;
      default:
        break;
    }
  }

  /**
   * Any activation inside the panel closes it — that is what a menu item is
   * for — unless the consumer opted into `stayOpen`, where each activation is
   * a toggle that applies in place. A disabled item never closes: native menus
   * leave the panel open when a dimmed row is clicked.
   */
  protected onPanelClick(event: MouseEvent): void {
    const item = (event.target as HTMLElement).closest('[appMenuItem]');
    if (!this.stayOpen() && item && item.getAttribute('aria-disabled') !== 'true') {
      this.close();
    }
  }

  private openMenu(): void {
    this.open.set(true);
  }

  protected close(returnFocus = true): void {
    if (!this.open()) {
      return;
    }
    this.closeOwnsFocus = true;
    this.open.set(false);
    if (returnFocus) {
      this.triggerRef().nativeElement.focus();
    }
  }

  private items(): HTMLElement[] {
    const panel = this.panelRef();
    return panel ? [...panel.nativeElement.querySelectorAll<HTMLElement>('[appMenuItem]')] : [];
  }

  /** `-1` focuses the last item. */
  private focusItem(index: number): void {
    const items = this.items();
    const target = index === -1 ? items[items.length - 1] : items[index];
    target?.focus();
  }
}
