import {
  Injectable,
  signal,
  computed,
  DestroyRef,
  inject,
} from '@angular/core';
import {
  Notification,
  NotificationConfig,
  NotificationType,
} from '../models/notification.model';

/**
 * Service for managing notifications/alerts throughout the application.
 * Uses Angular Signals for reactive state management.
 */
@Injectable({
  providedIn: 'root',
})
export class NotificationService {
  private readonly destroyRef = inject(DestroyRef);
  private timeoutIds = new Map<string, number>();

  private readonly notificationsSignal = signal<Notification[]>([]);
  private readonly configSignal = signal<NotificationConfig>({
    defaultTimeout: 5000,
    maxNotifications: 5,
    preventDuplicates: true,
    position: 'top',
  });

  /**
   * Read-only signal exposing the current notifications
   */
  readonly notifications = this.notificationsSignal.asReadonly();

  /**
   * Read-only signal exposing the current configuration
   */
  readonly config = this.configSignal.asReadonly();

  /**
   * Computed signal for the number of active notifications
   */
  readonly notificationCount = computed(
    () => this.notificationsSignal().length
  );

  /**
   * Computed signal for checking if there are any notifications
   */
  readonly hasNotifications = computed(
    () => this.notificationsSignal().length > 0
  );

  constructor() {
    // Cleanup all timeouts on service destroy
    this.destroyRef.onDestroy(() => {
      this.clearAllTimeouts();
    });
  }

  /**
   * Updates the notification service configuration
   */
  configure(config: Partial<NotificationConfig>): void {
    this.configSignal.update((current) => ({
      ...current,
      ...config,
    }));
  }

  /**
   * Shows a success notification
   */
  success(message: string, timeout?: number): void {
    this.addNotification(message, 'success', timeout);
  }

  /**
   * Shows an info notification
   */
  info(message: string, timeout?: number): void {
    this.addNotification(message, 'info', timeout);
  }

  /**
   * Shows a warning notification
   */
  warning(message: string, timeout?: number): void {
    this.addNotification(message, 'warning', timeout);
  }

  /**
   * Shows an error notification
   */
  error(message: string, timeout?: number): void {
    this.addNotification(message, 'danger', timeout);
  }

  /**
   * Removes a notification by its ID
   */
  dismiss(id: string): void {
    // Clear the timeout if it exists
    this.clearTimeout(id);

    this.notificationsSignal.update((notifications) =>
      notifications.filter((n) => n.id !== id)
    );
  }

  /**
   * Clears all notifications
   */
  clearAll(): void {
    this.clearAllTimeouts();
    this.notificationsSignal.set([]);
  }

  /**
   * Removes all notifications of a specific type
   */
  clearByType(type: NotificationType): void {
    const notificationsToClear = this.notificationsSignal().filter(
      (n) => n.type === type
    );
    notificationsToClear.forEach((n) => this.clearTimeout(n.id));

    this.notificationsSignal.update((notifications) =>
      notifications.filter((n) => n.type !== type)
    );
  }

  /**
   * Internal method to add a notification
   */
  private addNotification(
    message: string,
    type: NotificationType,
    timeout?: number
  ): void {
    const config = this.configSignal();

    // Check for duplicates if enabled
    if (config.preventDuplicates && this.hasDuplicate(message, type)) {
      return;
    }

    const notification: Notification = {
      id: this.generateId(),
      message,
      type,
      timeout: timeout ?? config.defaultTimeout,
      dismissible: true,
      timestamp: new Date(),
    };

    // Add the notification
    this.notificationsSignal.update((notifications) => {
      const updated = [...notifications, notification];

      // Enforce max notifications limit
      if (
        config.maxNotifications > 0 &&
        updated.length > config.maxNotifications
      ) {
        // Remove oldest notifications and their timeouts
        const removed = updated.slice(
          0,
          updated.length - config.maxNotifications
        );
        removed.forEach((n) => this.clearTimeout(n.id));
        return updated.slice(updated.length - config.maxNotifications);
      }

      return updated;
    });

    // Set up auto-dismiss if timeout is specified
    this.scheduleAutoDismiss(notification);
  }

  /**
   * Checks if a duplicate notification exists
   */
  private hasDuplicate(message: string, type: NotificationType): boolean {
    return this.notificationsSignal().some(
      (n) => n.message === message && n.type === type
    );
  }

  /**
   * Schedules auto-dismiss for a notification
   */
  private scheduleAutoDismiss(notification: Notification): void {
    if (notification.timeout && notification.timeout > 0) {
      const timeoutId = window.setTimeout(() => {
        this.dismiss(notification.id);
      }, notification.timeout);

      this.timeoutIds.set(notification.id, timeoutId);
    }
  }

  /**
   * Clears a specific timeout
   */
  private clearTimeout(id: string): void {
    const timeoutId = this.timeoutIds.get(id);
    if (timeoutId !== undefined) {
      window.clearTimeout(timeoutId);
      this.timeoutIds.delete(id);
    }
  }

  /**
   * Clears all active timeouts
   */
  private clearAllTimeouts(): void {
    this.timeoutIds.forEach((timeoutId) => window.clearTimeout(timeoutId));
    this.timeoutIds.clear();
  }

  /**
   * Generates a unique ID for a notification
   */
  private generateId(): string {
    return `notification-${Date.now()}-${Math.random()
      .toString(36)
      .substring(2, 9)}`;
  }
}
