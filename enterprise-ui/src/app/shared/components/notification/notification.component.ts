import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { NgClass } from '@angular/common';
import { NotificationService } from '../../../services/notification.service';
import { NotificationType } from '../../../models/notification.model';

/**
 * Component that displays notification alerts using Bootstrap styling.
 * Uses Angular Signals for reactive updates with OnPush change detection.
 */
@Component({
  selector: 'app-notification',
  imports: [NgClass],
  templateUrl: './notification.component.html',
  styleUrls: ['./notification.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationComponent {
  private readonly notificationService = inject(NotificationService);

  /**
   * Signal containing all active notifications
   */
  readonly notifications = this.notificationService.notifications;

  /**
   * Signal containing the notification service configuration
   */
  readonly config = this.notificationService.config;

  /**
   * Map of notification types to Bootstrap alert classes
   */
  private readonly alertClassMap: Record<NotificationType, string> = {
    success: 'alert-success',
    info: 'alert-info',
    warning: 'alert-warning',
    danger: 'alert-danger',
  };

  /**
   * Map of notification types to Bootstrap icon classes
   */
  private readonly iconClassMap: Record<NotificationType, string> = {
    success: 'bi-check-circle-fill',
    info: 'bi-info-circle-fill',
    warning: 'bi-exclamation-triangle-fill',
    danger: 'bi-x-circle-fill',
  };

  /**
   * Dismisses a notification by its ID
   */
  dismiss(id: string): void {
    this.notificationService.dismiss(id);
  }

  /**
   * Gets the Bootstrap alert class for a notification type
   */
  getAlertClass(type: NotificationType): string {
    return this.alertClassMap[type] || this.alertClassMap.info;
  }

  /**
   * Gets the Bootstrap icon for a notification type
   */
  getAlertIcon(type: NotificationType): string {
    return this.iconClassMap[type] || this.iconClassMap.info;
  }

  /**
   * Gets the appropriate ARIA live region value based on notification type
   */
  getAriaLive(type: NotificationType): 'assertive' | 'polite' {
    return type === 'danger' ? 'assertive' : 'polite';
  }

  /**
   * Gets a descriptive label for screen readers
   */
  getAriaLabel(type: NotificationType, message: string): string {
    const typeLabel = type.charAt(0).toUpperCase() + type.slice(1);
    return `${typeLabel} notification: ${message}`;
  }
}
