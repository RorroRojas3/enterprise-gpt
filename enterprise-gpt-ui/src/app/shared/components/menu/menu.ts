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
import { onDismiss } from '@shared/utils/a11y/dismiss';
import { IconName } from '@shared/utils/icons/icon-names';
import { AnchoredPosition, anchoredPosition } from '@shared/utils/overlay/anchored-panel';
import { Icon } from '@shared/components/icon/icon';
import { MenuTriggerContent } from '@shared/directives/menu-trigger-content';
import { Tooltip } from '@shared/directives/tooltip';

let nextId = 0;

/**
 * Hand-rolled rather than Bootstrap's dropdown, which bundles Popper (35.7 kB) to flip menus at
 * the viewport edge; these anchor to their row. A `position: fixed` panel also escapes the
 * sidebar's `overflow-y: auto`.
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
   * Name the subject ("Actions for Helios"), and the current value when the trigger projects its
   * own face ("Model: GPT-5 Enterprise"): the name is all a screen reader gets.
   */
  readonly label = input.required<string>();
  readonly icon = input<IconName>('bi-three-dots-vertical');
  readonly align = input<'start' | 'end'>('end');
  readonly open = model<boolean>(false);
  /**
   * Keeps the panel open on activation, for multi-select toggles. Escape, Tab, outside press and
   * scroll still dismiss.
   */
  readonly stayOpen = input<boolean>(false);
  /** `aria-disabled` plus inert, not the native attribute, so keyboard users can still focus it. */
  readonly disabled = input<boolean>(false);
  /**
   * A disabled menu's reason, as a tooltip on the trigger: on the role-less host it would fail
   * axe's `aria-prohibited-attr`. It stays visual, so put the reason in `label` too.
   */
  readonly hint = input<string | null>(null);
  readonly panelWidth = input<number>(204);
  /** `up` opens above the trigger, for the composer; static, never flipped at the viewport edge. */
  readonly direction = input<'down' | 'up'>('down');

  private readonly triggerContent = contentChild(MenuTriggerContent);
  protected readonly hasTriggerContent = computed(() => this.triggerContent() !== undefined);

  /**
   * The hint sits on the side the panel opens to: a `top` flyout on a trigger with no
   * room above it is clamped back over the control it is meant to describe.
   */
  protected readonly hintPlacement = computed(() => (this.direction() === 'up' ? 'top' : 'bottom'));

  protected readonly panelId = `menu-panel-${nextId++}`;
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
   * Set by {@link close}, which has already decided focus, so the fall-through rescue does not
   * override `close(false)` leaving focus on `<body>`.
   */
  private closeOwnsFocus = false;

  constructor() {
    // Driven by state, not by the method that set it, so a parent binding `[(open)]` still
    // gets outside-click and scroll handling, and its close aborts the listeners.
    effect((onCleanup) => {
      if (!this.open()) {
        return;
      }

      const controller = new AbortController();
      onDismiss(
        // The host, not the trigger: the panel is the trigger's sibling, so an item pointerdown
        // would read as outside and dismiss before the item's click fired.
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
      // earlyRead, not read: `read` runs after the `write` that needs the measurement.
      earlyRead: () => {
        if (!this.open() || !this.panelRef()) {
          return null;
        }
        // `clientWidth`, not `innerWidth`: a fixed panel's containing block excludes the classic
        // scrollbar, so `innerWidth` would overhang and add a horizontal scrollbar.
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
        // `panelWidth()`, not `offsetWidth`, which reflects the previous pass's width and would
        // give a first narrow open a stale inset.
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

    // While open, focus belongs in the panel: a content swap that unmounts the focused item drops
    // it to <body>, so that fall-through is corrected. A close that bypasses `close()` (a consumer
    // clearing `open`) returns focus to the trigger; `close()` decides for itself.
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
   * For anchoring another overlay where this menu was. A method, called at the gesture, because
   * a template binding would read the required `viewChild` before the view exists.
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
    // Focus can sit on the trigger while open (an itemless panel never steals it), so Escape
    // must work here too.
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

  /** Activation closes the panel unless `stayOpen`; a disabled item never does, as natively. */
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
