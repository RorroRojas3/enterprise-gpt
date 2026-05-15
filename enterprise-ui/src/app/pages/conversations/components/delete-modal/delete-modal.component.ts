import { Component, input, output, effect, signal } from '@angular/core';


@Component({
  selector: 'app-delete-modal',
  imports: [],
  templateUrl: './delete-modal.component.html',
  styleUrl: './delete-modal.component.scss',
})
export class DeleteModalComponent {
  show = input<boolean>(false);
  conversationCount = input<number>(0);
  onDelete = output<void>();
  onClose = output<void>();
  shouldShow = signal<boolean>(false);

  constructor() {
    // Use effect to respond to input changes
    effect(() => {
      if (this.show()) {
        setTimeout(() => {
          this.shouldShow.set(true);
        }, 10);
      } else {
        this.shouldShow.set(false);
      }
    });
  }

  /**
   * Handles the delete action by emitting the delete event.
   */
  handleDelete(): void {
    this.onDelete.emit();
  }

  /**
   * Handles the modal close action by emitting the close event.
   */
  handleClose(): void {
    this.onClose.emit();
  }
}
