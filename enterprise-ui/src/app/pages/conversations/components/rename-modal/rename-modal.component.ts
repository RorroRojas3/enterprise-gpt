import { Component, input, output, effect, signal } from '@angular/core';
import { ReactiveFormsModule, FormControl, Validators } from '@angular/forms';


@Component({
  selector: 'app-rename-modal',
  imports: [ReactiveFormsModule],
  templateUrl: './rename-modal.component.html',
  styleUrl: './rename-modal.component.scss',
})
export class RenameModalComponent {
  // Constants
  readonly MAX_NAME_LENGTH = 256;

  show = input<boolean>(false);
  name = input<string>('');
  onRename = output<string>();
  onClose = output<void>();
  shouldShow = signal<boolean>(false);

  conversationName = new FormControl('', [
    Validators.required,
    Validators.minLength(1),
    Validators.maxLength(this.MAX_NAME_LENGTH),
  ]);

  constructor() {
    // Use effect to respond to input changes
    effect(() => {
      if (this.show()) {
        this.conversationName.setValue(this.name());
        this.conversationName.markAsUntouched();
        this.conversationName.markAsPristine();
        setTimeout(() => {
          this.shouldShow.set(true);
        }, 10);
      } else {
        this.shouldShow.set(false);
      }
    });
  }

  /**
   * Determines whether to display validation errors.
   * Returns true if the form control is invalid and has been interacted with.
   */
  showError(): boolean {
    return (
      this.conversationName.invalid &&
      (this.conversationName.dirty || this.conversationName.touched)
    );
  }

  /**
   * Gets the current character count of the trimmed conversation name.
   * Returns 0 if the value is empty or null.
   */
  characterCount(): number {
    return this.conversationName.value?.trim().length || 0;
  }

  /**
   * Handles the rename action by emitting the trimmed conversation name value.
   */
  handleRename(): void {
    if (this.conversationName.valid && this.conversationName.value) {
      this.onRename.emit(this.conversationName.value.trim());
    }
  }

  /**
   * Handles the modal close action by emitting the close event.
   */
  handleClose(): void {
    this.onClose.emit();
  }
}
