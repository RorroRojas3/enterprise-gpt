import {
  Component,
  input,
  inject,
  signal,
  computed,
  AfterViewInit,
  ElementRef,
  viewChild,
  OnDestroy,
} from '@angular/core';
import { MessageDto } from '../../../dtos/MessageDto';
import { StoreService } from '../../../store/store.service';
import { NotificationService } from '../../../services/notification.service';

/** Duration in milliseconds for copy success feedback */
const COPY_FEEDBACK_DURATION_MS = 2000;

@Component({
  selector: 'app-message-bubble',
  imports: [],
  templateUrl: './message-bubble.component.html',
  styleUrl: './message-bubble.component.scss',
})
export class MessageBubbleComponent implements AfterViewInit, OnDestroy {
  readonly message = input.required<MessageDto>();
  readonly storeService = inject(StoreService);
  readonly notificationService = inject(NotificationService);

  private readonly messageContentRef =
    viewChild<ElementRef<HTMLDivElement>>('messageContent');

  /** Tracks whether the full message was recently copied */
  readonly messageCopied = signal(false);

  /** Determines if the copy message button should be shown */
  readonly canCopyMessage = computed(
    () => !this.message().isOutgoing && !this.storeService.isStreaming()
  );

  /** Stores active timeout IDs for cleanup on destroy */
  private readonly activeTimeouts = new Set<ReturnType<typeof setTimeout>>();

  /** Stores button event handlers for cleanup on destroy */
  private readonly buttonEventHandlers = new Map<
    HTMLButtonElement,
    {
      click: () => Promise<void>;
      mouseenter: () => void;
      mouseleave: () => void;
    }
  >();

  ngAfterViewInit(): void {
    this.addCodeBlockCopyButtons();
  }

  ngOnDestroy(): void {
    this.clearAllTimeouts();
    this.removeAllEventListeners();
  }

  /**
   * Copies the entire message content to clipboard.
   * Shows success feedback for a brief duration.
   */
  async copyMessage(): Promise<void> {
    const content = this.message().content;
    if (!content) return;

    try {
      await navigator.clipboard.writeText(content);
      this.showCopyFeedback();
    } catch (error: unknown) {
      this.notificationService.error('Failed to copy message to clipboard.');
    }
  }

  /**
   * Shows temporary success feedback after copying.
   */
  private showCopyFeedback(): void {
    this.messageCopied.set(true);
    const timeoutId = setTimeout(() => {
      this.messageCopied.set(false);
      this.activeTimeouts.delete(timeoutId);
    }, COPY_FEEDBACK_DURATION_MS);
    this.activeTimeouts.add(timeoutId);
  }

  /**
   * Adds copy buttons to all code blocks within the message content.
   * Each button provides clipboard functionality with visual feedback.
   */
  private addCodeBlockCopyButtons(): void {
    const contentEl = this.messageContentRef()?.nativeElement;
    if (!contentEl) return;

    const preElements = contentEl.querySelectorAll('pre');
    preElements.forEach((pre) => this.wrapCodeBlockWithCopyButton(pre));
  }

  /**
   * Wraps a pre element with a container and adds a copy button.
   */
  private wrapCodeBlockWithCopyButton(pre: HTMLPreElement): void {
    // Skip if already wrapped
    if (pre.parentElement?.classList.contains('code-block-wrapper')) return;

    // Create wrapper for positioning
    const wrapper = document.createElement('div');
    wrapper.className = 'code-block-wrapper';
    Object.assign(wrapper.style, {
      position: 'relative',
      display: 'block',
    });

    // Insert wrapper and move pre inside
    pre.parentNode?.insertBefore(wrapper, pre);
    wrapper.appendChild(pre);

    const copyBtn = this.createCopyButton();

    // Create all handlers
    const clickHandler = (): Promise<void> =>
      this.handleCodeBlockCopy(pre, copyBtn);
    const mouseenterHandler = (): void => {
      if (!copyBtn.classList.contains('copied')) {
        copyBtn.style.opacity = '1';
        copyBtn.style.backgroundColor = 'var(--bg-primary)';
        copyBtn.style.borderColor = 'var(--border-strong)';
        copyBtn.style.color = 'var(--text-primary)';
      }
    };
    const mouseleaveHandler = (): void => {
      if (!copyBtn.classList.contains('copied')) {
        copyBtn.style.opacity = '0.7';
        copyBtn.style.backgroundColor = 'var(--bg-primary)';
        copyBtn.style.borderColor = 'var(--border-default)';
        copyBtn.style.color = 'var(--text-secondary)';
      }
    };

    // Add event listeners
    copyBtn.addEventListener('click', clickHandler);
    copyBtn.addEventListener('mouseenter', mouseenterHandler);
    copyBtn.addEventListener('mouseleave', mouseleaveHandler);

    // Store for cleanup
    this.buttonEventHandlers.set(copyBtn, {
      click: clickHandler,
      mouseenter: mouseenterHandler,
      mouseleave: mouseleaveHandler,
    });

    wrapper.appendChild(copyBtn);
  }

  /**
   * Creates a copy button element with proper styling and accessibility.
   */
  private createCopyButton(): HTMLButtonElement {
    const btn = document.createElement('button');
    btn.className = 'code-copy-btn';
    btn.type = 'button';
    btn.setAttribute('aria-label', 'Copy code');
    btn.setAttribute('title', 'Copy code');
    btn.innerHTML = '<i class="bi bi-clipboard"></i>';

    // Apply inline styles - positioned at top-right corner of wrapper
    Object.assign(btn.style, {
      position: 'absolute',
      top: '0.5rem',
      right: '0.5rem',
      opacity: '0.7',
      transition: 'opacity 0.2s ease, background-color 0.2s ease',
      backgroundColor: 'var(--bg-primary)',
      border: '1px solid var(--border-default)',
      color: 'var(--text-secondary)',
      padding: '0.25rem 0.5rem',
      zIndex: '1',
      fontSize: '0.75rem',
      borderRadius: '0.25rem',
      cursor: 'pointer',
    });

    return btn;
  }

  /**
   * Handles the copy action for a code block with visual feedback.
   */
  private async handleCodeBlockCopy(
    pre: HTMLPreElement,
    btn: HTMLButtonElement
  ): Promise<void> {
    const codeEl = pre.querySelector('code');
    const codeText = codeEl?.textContent || pre.textContent || '';

    try {
      await navigator.clipboard.writeText(codeText);
      this.showCodeBlockCopyFeedback(btn);
    } catch (error) {
      console.error('Failed to copy code:', error);
    }
  }

  /**
   * Shows temporary success feedback on the code block copy button.
   */
  private showCodeBlockCopyFeedback(btn: HTMLButtonElement): void {
    btn.innerHTML = '<i class="bi bi-clipboard-check"></i>';
    btn.classList.add('copied');
    btn.setAttribute('aria-label', 'Code copied');
    btn.setAttribute('title', 'Copied!');

    // Apply copied styles
    btn.style.opacity = '1';
    btn.style.backgroundColor = '#d4edda';
    btn.style.borderColor = '#28a745';
    btn.style.color = '#28a745';

    const timeoutId = setTimeout(() => {
      btn.innerHTML = '<i class="bi bi-clipboard"></i>';
      btn.classList.remove('copied');
      btn.setAttribute('aria-label', 'Copy code');
      btn.setAttribute('title', 'Copy code');

      // Reset styles
      btn.style.opacity = '0.7';
      btn.style.backgroundColor = 'var(--bg-primary)';
      btn.style.borderColor = 'var(--border-default)';
      btn.style.color = 'var(--text-secondary)';

      this.activeTimeouts.delete(timeoutId);
    }, COPY_FEEDBACK_DURATION_MS);
    this.activeTimeouts.add(timeoutId);
  }

  /**
   * Clears all active timeouts to prevent memory leaks.
   */
  private clearAllTimeouts(): void {
    this.activeTimeouts.forEach((timeoutId) => clearTimeout(timeoutId));
    this.activeTimeouts.clear();
  }

  /**
   * Removes all event listeners from dynamically created buttons.
   */
  private removeAllEventListeners(): void {
    this.buttonEventHandlers.forEach((handlers, btn) => {
      btn.removeEventListener('click', handlers.click);
      btn.removeEventListener('mouseenter', handlers.mouseenter);
      btn.removeEventListener('mouseleave', handlers.mouseleave);
    });
    this.buttonEventHandlers.clear();
  }
}
