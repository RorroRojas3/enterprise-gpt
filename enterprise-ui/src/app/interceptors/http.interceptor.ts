import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError } from 'rxjs/operators';
import { throwError } from 'rxjs';
import { inject } from '@angular/core';
import { NotificationService } from '../services/notification.service';
import { ErrorDto } from '../dtos/ErrorDto';
import { environment } from '../../environments/environment';

export const httpInterceptor: HttpInterceptorFn = (req, next) => {
  const notificationService = inject(NotificationService);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.error instanceof ErrorEvent) {
        // Client-side or network error
        notificationService.error(`Network error: ${error.error.message}`);
      } else {
        // Try to extract errors from ErrorDto
        const apiErrors = extractErrorsFromDto(error);

        if (apiErrors && apiErrors.length > 0) {
          // Display each error as a separate notification
          apiErrors.forEach((errorMsg) => {
            displayErrorByStatus(error.status, errorMsg, notificationService);
          });
        } else {
          // No ErrorDto, use default message
          displayErrorByStatus(error.status, null, notificationService);
        }

        if (!environment.production) {
          logErrorDetails(error);
        }
      }

      // Return the error to allow components to handle it if needed
      return throwError(() => error);
    })
  );
};

/**
 * Displays error notification based on HTTP status code
 */
function displayErrorByStatus(
  status: number,
  message: string | null,
  notificationService: NotificationService
): void {
  let errorMessage: string;

  switch (status) {
    case 400:
      errorMessage =
        message || 'Bad request. Please check your input and try again.';
      notificationService.error(errorMessage);
      break;
    case 401:
      errorMessage = message || 'Unauthorized, you must log in again.';
      notificationService.error(errorMessage);
      break;
    case 403:
      errorMessage =
        message ||
        'Forbidden. You do not have permission to access this resource.';
      notificationService.error(errorMessage);
      break;
    case 404:
      errorMessage = message || 'Resource not found.';
      notificationService.error(errorMessage);
      break;
    case 408:
      errorMessage = message || 'Request timeout. Please try again.';
      notificationService.warning(errorMessage);
      break;
    case 422:
      errorMessage = message || 'Validation error. Please check your input.';
      notificationService.error(errorMessage);
      break;
    case 429:
      errorMessage = message || 'Too many requests. Please wait and try again.';
      notificationService.warning(errorMessage);
      break;
    case 500:
      errorMessage =
        message ||
        'An unexpected error occurred on the server. Please try again later.';
      notificationService.error(errorMessage);
      break;
    case 502:
      errorMessage =
        message || 'Bad gateway. The server is temporarily unavailable.';
      notificationService.error(errorMessage);
      break;
    case 503:
      errorMessage = message || 'Service unavailable. Please try again later.';
      notificationService.error(errorMessage);
      break;
    case 504:
      errorMessage =
        message || 'Gateway timeout. The server took too long to respond.';
      notificationService.error(errorMessage);
      break;
    case 0:
      errorMessage =
        'Unable to connect to the server. Please check your internet connection.';
      notificationService.error(errorMessage);
      break;
    default:
      errorMessage = message || `An error occurred: ${status} - Unknown error`;
      notificationService.error(errorMessage);
      break;
  }
}

/**
 * Extracts error messages array from ErrorDto structure
 */
function extractErrorsFromDto(error: HttpErrorResponse): string[] | null {
  if (!error.error) {
    return null;
  }

  const errorDto = error.error as Partial<ErrorDto>;

  // Check if the error response matches ErrorDto structure
  if (
    errorDto.errors &&
    Array.isArray(errorDto.errors) &&
    errorDto.errors.length > 0
  ) {
    return errorDto.errors;
  }

  return null;
}

/**
 * Logs detailed error information to console for debugging
 */
function logErrorDetails(error: HttpErrorResponse): void {
  if (!error.error) {
    return;
  }

  const errorDto = error.error as Partial<ErrorDto>;

  console.group(`HTTP Error ${error.status}`);

  if (errorDto.errors && errorDto.errors.length > 0) {
    console.error('Errors:', errorDto.errors);
  }

  if (errorDto.traceId) {
    console.error('Trace ID:', errorDto.traceId);
  }

  if (errorDto.timestamp) {
    console.error('Timestamp:', errorDto.timestamp);
  }

  console.groupEnd();
}
