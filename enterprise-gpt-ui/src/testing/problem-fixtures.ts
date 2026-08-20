import { AnyProblemDetails, ProblemDetails } from '../app/core/errors/problem-details';
import { PROBLEM_TYPE } from '../app/core/errors/problem-types';

/** A representative W3C trace id, in the 32-hex form the API emits. */
export const TRACE_ID = '4bf92f3577b34da6a3ce929d0e0e4736';

/**
 * One canned problem body per application-specific type, shaped exactly as the
 * API writes them — including the extension members and the null `detail` on
 * validation problems.
 */
export const PROBLEM_FIXTURES = {
  validationError: {
    type: PROBLEM_TYPE.validationError,
    title: 'One or more validation errors occurred.',
    status: 400,
    detail: null,
    instance: '/api/models',
    traceId: TRACE_ID,
    errors: {
      Name: ["'Name' must not be empty."],
      ContextWindowSize: ["'Context Window Size' must be greater than 0."],
    },
  },
  uploadTooLarge: {
    type: PROBLEM_TYPE.uploadTooLarge,
    title: 'Upload too large',
    status: 400,
    detail: 'The file exceeds the maximum upload size of 50 MB.',
    instance: '/api/documents/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
    traceId: TRACE_ID,
    maxBytes: 52_428_800,
  },
  resourceNotFound: {
    type: PROBLEM_TYPE.resourceNotFound,
    title: 'Resource not found',
    status: 404,
    detail: 'Conversation not found.',
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
    traceId: TRACE_ID,
  },
  forbidden: {
    type: PROBLEM_TYPE.forbidden,
    title: 'Access denied',
    status: 403,
    detail: 'Access denied.',
    instance: '/api/conversations',
    traceId: TRACE_ID,
  },
  permissionRequired: {
    type: PROBLEM_TYPE.permissionRequired,
    title: 'Permission required',
    status: 403,
    detail: 'Upload File permission is required.',
    instance: '/api/documents/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
    traceId: TRACE_ID,
    permissions: ['Upload File'],
  },
  conversationBusy: {
    type: PROBLEM_TYPE.conversationBusy,
    title: 'Conversation is busy',
    status: 409,
    detail: 'A response is already being generated for this conversation.',
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/stream',
    traceId: TRACE_ID,
  },
  mcpAuthorizationRequired: {
    type: PROBLEM_TYPE.mcpAuthorizationRequired,
    title: 'MCP authorization required',
    status: 403,
    detail: "Consent or additional authentication is required for MCP server 'Weather'.",
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/stream',
    traceId: TRACE_ID,
    serverName: 'Weather',
  },
  mcpServerUnavailable: {
    type: PROBLEM_TYPE.mcpServerUnavailable,
    title: 'MCP server unavailable',
    status: 502,
    detail: "MCP server 'Weather' is unavailable.",
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/stream',
    traceId: TRACE_ID,
    serverName: 'Weather',
  },
  providerNotConfigured: {
    type: PROBLEM_TYPE.providerNotConfigured,
    title: 'Provider not configured',
    status: 503,
    detail: "No chat client is configured for provider 'c1a2b3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d'.",
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/stream',
    traceId: TRACE_ID,
    providerId: 'c1a2b3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d',
  },
  storageNotConfigured: {
    type: PROBLEM_TYPE.storageNotConfigured,
    title: 'Storage not configured',
    status: 503,
    detail: 'Document storage is not configured correctly.',
    instance: '/api/documents/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/download',
    traceId: TRACE_ID,
  },
  exportRendererNotConfigured: {
    type: PROBLEM_TYPE.exportRendererNotConfigured,
    title: 'Export format not available',
    status: 503,
    detail: 'Conversation export to pdf is not available in this environment.',
    instance: '/api/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b/export',
    traceId: TRACE_ID,
    format: 'pdf',
  },
  // `satisfies`, not an annotation: it catches a dropped `title` or a mistyped
  // `status` while keeping each fixture's extension members visible to callers.
} as const satisfies Record<string, AnyProblemDetails>;

/**
 * Framework-level problems, which carry an RFC 9110 status-section link instead
 * of an application type. A client must not mistake these for domain failures.
 */
export const FRAMEWORK_PROBLEM_FIXTURES = {
  unauthorized: {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2',
    title: 'Unauthorized',
    status: 401,
    instance: '/api/conversations',
    traceId: TRACE_ID,
  },
  routeNotFound: {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
    title: 'Not Found',
    status: 404,
    instance: '/api/nope',
    traceId: TRACE_ID,
  },
  payloadTooLarge: {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.14',
    title: 'Content Too Large',
    status: 413,
    detail: 'Request body too large.',
    instance: '/api/documents/conversations/6f9d1c1e-0b2a-4e3f-9a1b-2c3d4e5f6a7b',
    traceId: TRACE_ID,
  },
  serverError: {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
    title: 'An error occurred while processing your request.',
    status: 500,
    detail: 'An unexpected internal server error occurred.',
    instance: '/api/conversations',
    traceId: TRACE_ID,
  },
} as const satisfies Record<string, ProblemDetails>;
