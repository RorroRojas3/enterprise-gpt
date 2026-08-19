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
import { ConversationDto } from '@domain/api/conversation';
import { ConversationActionsStore } from '@core/conversations/conversation-actions-store';
import { matchesProjectTerm } from '@core/projects/project-load';
import { ProjectActionsStore } from '@core/projects/project-actions-store';
import { ProjectLookupStore } from '@core/projects/project-lookup-store';
import { onDismiss } from '@shared/a11y/dismiss';
import { Icon } from '@shared/icon/icon';
import { AnchoredPosition, anchoredPosition } from '@shared/overlay/anchored-panel';

let nextId = 0;

/**
 * The panel's width, mirroring `.picker` in the stylesheet. Named here as well so the
 * placement can decide whether it still fits the viewport; the inline width the
 * full-width branch returns wins over the CSS one, and `null` leaves it alone.
 */
const PANEL_WIDTH = 320;

/**
 * Frame `2e`: the searchable project list, with "Remove from project" and a pinned
 * "New project" (US-307).
 *
 * **One control, three invokers**, which is the board's own note on the frame — "same
 * control as the conversation header's Move to project". The sidebar row kebab and the
 * chat header kebab open it anchored to their own trigger; the composer's project pill
 * opens it anchored to itself.
 *
 * **It is not a `Menu`, deliberately.** `Menu` renders its own trigger and its panel is
 * `role="menu"`, whose only valid children are `menuitem*` — and its keyboard handling
 * swallows the arrow keys and closes on Tab. A search field inside that is three
 * separate collisions. So this is a non-modal `role="dialog"` holding a listbox, and
 * what it borrows from `Menu` is the part that genuinely is shared: `anchoredPosition`
 * for placement and `onDismiss` for outside-press and scroll.
 *
 * It lives in `shared/` and injects only `core/` stores, exactly as `Composer` does.
 */
@Component({
  selector: 'app-project-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './project-picker.html',
  styleUrl: './project-picker.scss',
})
export class ProjectPicker {
  private readonly document = inject(DOCUMENT);
  private readonly hostRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly panelRef = viewChild<ElementRef<HTMLDivElement>>('panel');
  private readonly fieldRef = viewChild<ElementRef<HTMLInputElement>>('field');

  protected readonly projects = inject(ProjectLookupStore);
  private readonly actions = inject(ConversationActionsStore);
  private readonly projectActions = inject(ProjectActionsStore);

  /** The conversation being moved. */
  readonly conversation = input.required<ConversationDto>();
  /**
   * The element the panel is placed against, and the one focus returns to.
   *
   * The kebab path passes its own trigger button, because the menu that opened this has
   * already closed and taken its own anchor out of the layout with it.
   */
  readonly anchor = input.required<HTMLElement>();
  readonly open = model<boolean>(false);
  readonly direction = input<'down' | 'up'>('down');
  readonly align = input<'start' | 'end'>('start');
  /**
   * Whether the anchor is this panel's own toggle (the composer pill) rather than a
   * separate control that happened to open it (a kebab item).
   *
   * It decides what a press on the anchor means. A toggle must not read as an outside
   * press, or the panel would close on `pointerdown` and reopen on the trigger's own
   * `click`. A kebab trigger is not a toggle: pressing it should close this and open the
   * menu, which is the default.
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
   * The filter term, local to this instance.
   *
   * Not `withClientQuery` on the lookup store: three pickers can exist at once — a
   * sidebar row, the chat header, the composer — and a term shared through root state
   * would narrow all of them from whichever one is being typed in. It is also transient
   * UI state that must not survive the panel closing.
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

  /** Whether the conversation is in a project at all — "Remove" is absent when it is not. */
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
      // earlyRead → write, as `Menu` does: the phases run earlyRead → write →
      // mixedReadWrite → read, so measuring in `read` would land after the write that
      // needs the measurement.
      earlyRead: () => {
        if (!this.open() || !this.panelRef()) {
          return null;
        }

        // Both reads happen here rather than in `write`, as `Menu` does: they are both
        // layout reads, and the phase split exists to keep them out of the write pass.
        // `documentElement.clientWidth` for the width, deliberately, and `innerWidth`
        // for nothing: a `position: fixed` panel's containing block **excludes** the
        // classic scrollbar, so sizing it from `innerWidth` overhangs by the scrollbar's
        // thickness and produces the horizontal scrollbar US-1403's last criterion
        // forbids — on exactly the desktop browser someone narrows to check it. It is
        // the JS spelling of the `100vw` mistake `offcanvas.scss` calls out one file
        // over.
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

        // PANEL_WIDTH rather than the measured width, for the reason `Menu` gives: the
        // measurement lags a pass behind the width that was bound.
        this.position.set(
          anchoredPosition(box.anchor, PANEL_WIDTH, box.viewport, {
            direction: this.direction(),
            align: this.align(),
          }),
        );
      },
    });

    // Focus the field once per opening, not on every render: the list re-renders as the
    // reader types, and re-focusing there would fight the caret rather than help it.
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
   * The panel's keyboard model, which is `Menu`'s adapted to a panel whose first child
   * is a text field.
   *
   * **The field is the only tab stop.** Every row carries `tabindex="-1"` and is reached
   * with the arrows, because the drain ceiling is 500 projects and an empty term matches
   * all of them — 500 tab stops inside one popover is not a navigable control. Tab
   * therefore leaves the panel, and closing on the way out is what keeps a 320px overlay
   * from floating over a page whose Escape handler it no longer owns.
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
        // Leaves rather than cycling: this is a non-modal popover, and the page behind
        // it is still live.
        this.close(false);
        break;
      default:
        break;
    }
  }

  /**
   * Closes when focus lands outside the panel by any route the keydown handler does not
   * see — a click into the page, a screen reader's own navigation, a Tab out of the
   * field on a browser that reports it here first.
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

    return panel ? [...panel.nativeElement.querySelectorAll<HTMLElement>('.picker__option')] : [];
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
   * Opens the shared create-project modal and moves the conversation into whatever it
   * creates — `beginCreate`'s continuation, which has existed unused since US-903 for
   * exactly this. The panel closes first: the dialog is where the reader is now, and a
   * popover left open behind a modal is a second dismissal target.
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
