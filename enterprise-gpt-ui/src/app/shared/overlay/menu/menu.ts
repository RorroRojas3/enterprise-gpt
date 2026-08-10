import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
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

  /** The accessible name of the trigger. Name the subject: "Actions for Helios". */
  readonly label = input.required<string>();
  readonly icon = input<IconName>('bi-three-dots-vertical');
  readonly align = input<'start' | 'end'>('end');
  readonly open = model<boolean>(false);

  protected readonly panelId = `menu-panel-${nextId++}`;
  protected readonly position = signal({ top: 0, left: 0 });

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
        this.position.set({
          top: box.bottom + 4,
          left: this.align() === 'end' ? box.right - width : box.left,
        });
        this.focusItem(this.focusLastOnOpen ? -1 : 0);
        this.focusLastOnOpen = false;
      },
    });

    this.destroyRef.onDestroy(() => this.close(false));
  }

  protected toggle(): void {
    if (this.open()) {
      this.close();
    } else {
      this.openMenu();
    }
  }

  protected onTriggerKeydown(event: KeyboardEvent): void {
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

  /** Any activation inside the panel closes it — that is what a menu item is for. */
  protected onPanelClick(event: MouseEvent): void {
    if ((event.target as HTMLElement).closest('[appMenuItem]')) {
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
