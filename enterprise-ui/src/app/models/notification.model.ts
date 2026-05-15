/**
 * Type of notification alert
 */
export type NotificationType = 'success' | 'info' | 'warning' | 'danger';

/**
 * Represents a single notification/alert message
 */
export interface Notification {
  /**
   * Unique identifier for the notification
   */
  id: string;

  /**
   * Message content to display
   */
  message: string;

  /**
   * Type of notification (maps to Bootstrap alert classes)
   */
  type: NotificationType;

  /**
   * Auto-dismiss timeout in milliseconds (0 or undefined means no auto-dismiss)
   */
  timeout?: number;

  /**
   * Whether the notification can be manually dismissed
   */
  dismissible: boolean;

  /**
   * Timestamp when the notification was created
   */
  timestamp: Date;
}

/**
 * Configuration options for the notification service
 */
export interface NotificationConfig {
  /**
   * Default timeout for auto-dismissing notifications (in milliseconds)
   * Set to 0 to disable auto-dismiss by default
   */
  defaultTimeout: number;

  /**
   * Maximum number of notifications to display simultaneously
   * Set to 0 for unlimited
   */
  maxNotifications: number;

  /**
   * Whether to prevent duplicate notifications
   */
  preventDuplicates: boolean;

  /**
   * Position of notifications container ('top' or 'bottom')
   */
  position: 'top' | 'bottom';
}
