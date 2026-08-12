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
  imports: [Icon],
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
  /**
   * `up` panels anchor by `bottom`, not a measured `top`: the pickers swap
   * panel content while open (retry → loading → rows), and a top-anchored
   * panel would grow *downward* over the trigger it is supposed to sit above.
   * Bottom-anchoring makes growth extend upward with no re-measurement.
   */
  protected readonly position = signal<{ top: number | null; bottom: number | null; left: number }>(
    { top: 0, bottom: null, left: 0 },
  );

  /** Set when opening by keyboard, so focus lands on the right end of the list. */
  private focusLastOnOpen = false;

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
        return this.triggerRef().nativeElement.getBoundingClientRect();
      },
      write: (rect) => {
        const box = rect();
        const panel = this.panelRef();
        if (!box || !panel) {
          return;
        }
        const width = panel.nativeElement.offsetWidth;
        const up = this.direction() === 'up';
        // The viewport, not documentElement.clientHeight: a fixed-position
        // `bottom` resolves against the viewport, and the two differ by the
        // horizontal scrollbar when one exists.
        const viewportHeight =
          this.document.defaultView?.innerHeight ?? this.document.documentElement.clientHeight;
        this.position.set({
          top: up ? null : box.bottom + 4,
          bottom: up ? viewportHeight - box.top + 4 : null,
          left: this.align() === 'end' ? box.right - width : box.left,
        });
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
    afterEveryRender({
      mixedReadWrite: () => {
        if (!this.open()) {
          return;
        }
        const panel = this.panelRef()?.nativeElement;
        const active = this.document.activeElement;
        if (!panel || (active !== null && active !== this.document.body && active.isConnected)) {
          return;
        }
        (this.items()[0] ?? panel).focus();
      },
    });

    this.destroyRef.onDestroy(() => this.close(false));
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
