import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  input,
  model,
  signal,
  viewChild,
} from '@angular/core';
import { ConversationDto } from '@core/api/conversation.model';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { Icon } from '@shared/icons/icon';
import { AnchoredPosition, anchoredPosition } from '@shared/overlay/anchored-panel';
import { onDismiss } from '@shared/overlay/dismiss';
import { ProjectActionsStore } from './project-actions-store';
import { matchesProjectTerm } from './project-load';
import { ProjectLookupStore } from './project-lookup-store';

let nextId = 0;

/** Also read by placement, to decide whether the panel still fits the viewport. */
const PANEL_WIDTH = 320;

/**
 * Not a `Menu`: `role="menu"` allows only menu items, and its keys would swallow the search
 * field's arrows and Tab. A non-modal dialog holding a listbox, borrowing `anchoredPosition`
 * and `onDismiss`.
 */
@Component({
  selector: 'app-project-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './project-picker.html',
})
export class ProjectPicker {
  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly panelRef = viewChild<ElementRef<HTMLDivElement>>('panel');
  private readonly fieldRef = viewChild<ElementRef<HTMLInputElement>>('field');

  protected readonly projects = inject(ProjectLookupStore);
  private readonly actions = inject(ConversationActionsStore);
  private readonly projectActions = inject(ProjectActionsStore);

  readonly conversation = input.required<ConversationDto>();
  /**
   * Where the panel is placed and focus returns. A kebab passes its own trigger, since the
   * menu that opened this has already closed.
   */
  readonly anchor = input.required<HTMLElement>();
  readonly open = model<boolean>(false);
  readonly direction = input<'down' | 'up'>('down');
  readonly align = input<'start' | 'end'>('start');
  /**
   * True for the composer pill, a toggle: a press on it must not read as outside, or the panel
   * would close on `pointerdown` and reopen on `click`. A kebab trigger is not a toggle.
   */
  readonly anchorToggles = input<boolean>(false);

  protected readonly panelId = `project-picker-${nextId++}`;
  protected readonly listId = `${this.panelId}-list`;
  /** Bound in the template, so the width lives here alone rather than here and in CSS. */
  protected readonly panelWidth = PANEL_WIDTH;

  protected readonly position = signal<AnchoredPosition>({
    top: 0,
    bottom: null,
    left: 0,
    width: null,
  });

  /**
   * Local rather than on the root lookup store: up to three pickers coexist, and a shared term
   * would narrow all of them.
   */
  protected readonly term = signal('');

  protected readonly matches = computed(() => {
    const needle = this.term();

    return this.projects.projects().filter((project) => matchesProjectTerm(project.name, needle));
  });

  /** Whether the listbox has anything to own. It is not rendered otherwise. */
  protected readonly hasOptions = computed(() => this.matches().length > 0);

  protected readonly hasNoMatches = computed(
    () => this.matches().length === 0 && this.projects.projects().length > 0,
  );

  protected readonly isAssigned = computed(() => this.conversation().projectId !== null);

  constructor() {
    // Driven by the state rather than by whichever method set it, so a parent binding
    // `[(open)]` cannot open a panel with no dismissal wired to it.
    effect((onCleanup) => {
      if (!this.open()) {
        return;
      }

      const controller = new AbortController();
      onDismiss(
        this.hostRef.nativeElement,
        {
          onOutside: () => this.close(false),
          onScroll: () => this.close(false),
          insideAlso: () => (this.anchorToggles() ? this.anchor() : null),
        },
        controller.signal,
      );

      onCleanup(() => controller.abort());
    });

    afterRenderEffect({
      earlyRead: () => {
        if (!this.open() || !this.panelRef()) {
          return null;
        }

        // `clientWidth`, not `innerWidth`: a fixed panel's containing block excludes the
        // classic scrollbar, so `innerWidth` would overhang and add a horizontal scrollbar.
        const view = this.document.defaultView;
        return {
          anchor: this.anchor().getBoundingClientRect(),
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

        // PANEL_WIDTH, not the measured width, which lags a pass behind the bound one.
        this.position.set(
          anchoredPosition(box.anchor, PANEL_WIDTH, box.viewport, {
            direction: this.direction(),
            align: this.align(),
          }),
        );
      },
    });

    // Once per opening: re-focusing on every render would fight the caret as the reader types.
    effect(() => {
      if (this.open()) {
        this.fieldRef()?.nativeElement.focus();
      }
    });

    inject(DestroyRef).onDestroy(() => this.open.set(false));
  }

  protected onTerm(event: Event): void {
    this.term.set((event.target as HTMLInputElement).value);
  }

  /**
   * The field is the only tab stop: rows are reached by arrow, since an empty term can list
   * 500 projects. Tab leaves and closes, so the overlay never outlives its Escape handler.
   */
  protected onKeydown(event: KeyboardEvent): void {
    const rows = this.rows();

    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        this.close();
        break;
      case 'ArrowDown':
        event.preventDefault();
        this.focusRow(this.activeRow(rows) + 1, rows);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusRow(this.activeRow(rows) - 1, rows);
        break;
      case 'Home':
        // Only from a row: inside the field, Home means "start of the text".
        if (this.activeRow(rows) >= 0) {
          event.preventDefault();
          this.focusRow(0, rows);
        }
        break;
      case 'End':
        if (this.activeRow(rows) >= 0) {
          event.preventDefault();
          this.focusRow(rows.length - 1, rows);
        }
        break;
      case 'Tab':
        // Leaves rather than cycling: the popover is non-modal and the page behind is live.
        this.close(false);
        break;
      default:
        break;
    }
  }

  /**
   * Closes when focus leaves by a route the keydown handler misses, such as a click into the
   * page or a screen reader's own navigation.
   */
  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget;
    if (next !== null && !(next instanceof Node)) {
      return;
    }

    const host = this.hostRef.nativeElement;
    if (next !== null && (host.contains(next) || this.anchor().contains(next))) {
      return;
    }

    this.close(false);
  }

  private rows(): HTMLElement[] {
    const panel = this.panelRef();

    return panel ? [...panel.nativeElement.querySelectorAll<HTMLElement>('[data-picker-row]')] : [];
  }

  /** The index of the focused row, or -1 when focus is in the search field. */
  private activeRow(rows: HTMLElement[]): number {
    return rows.indexOf(this.document.activeElement as HTMLElement);
  }

  /** Wraps, and returns to the field when moving up off the first row. */
  private focusRow(index: number, rows: HTMLElement[]): void {
    if (rows.length === 0) {
      return;
    }

    if (index < 0) {
      this.fieldRef()?.nativeElement.focus();
      return;
    }

    rows[index % rows.length]?.focus();
  }

  /** Moves the conversation into `projectId`, or out of every project when null. */
  protected select(projectId: string | null): void {
    this.actions.moveToProject(this.conversation(), projectId);
    this.close();
  }

  /**
   * Moves the conversation into whatever the create modal makes. Closes first, so no popover
   * is left behind the modal as a second dismissal target.
   */
  protected createProject(): void {
    const conversation = this.conversation();
    this.close();
    this.projectActions.beginCreate((project) =>
      this.actions.moveToProject(conversation, project.id),
    );
  }

  protected close(returnFocus = true): void {
    if (!this.open()) {
      return;
    }

    this.open.set(false);
    this.term.set('');
    if (returnFocus && this.anchor().isConnected) {
      this.anchor().focus();
    }
  }
}
