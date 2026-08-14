import { describe, expect, it } from 'vitest';
import { FRAMEWORK_PROBLEM_FIXTURES, PROBLEM_FIXTURES } from '@testing/problem-fixtures';
import { AppError } from '@core/errors/app-error';
import { buildAppError } from '@core/errors/build-app-error';
import { AnyProblemDetails } from '@core/errors/problem-details';
import { describeUploadRejection } from './upload-rejection';

function errorFrom(problem: AnyProblemDetails): AppError {
  return buildAppError({ status: problem.status, problem, url: null, cause: null });
}

const HUGE = { name: 'web-archive.har', size: 222_298_112 };

describe('describeUploadRejection', () => {
  it('quotes both numbers for a size refusal, as frame 4l does', () => {
    const rejection = describeUploadRejection(errorFrom(PROBLEM_FIXTURES.uploadTooLarge), HUGE);

    expect(rejection.title).toBe('web-archive.har couldn’t be uploaded');
    expect(rejection.detail).toBe('Files can be up to 50 MB — this one is 212 MB.');
    expect(rejection.traceLine).toContain('4bf92f3577b34da6a3ce929d0e0e4736');
    expect(rejection.retryable).toBe(true);
  });

  it('states the file size alone for a bare 413, inventing no limit', () => {
    // Kestrel's own response, from a chunked upload that declared no length: it
    // carries no maxBytes, and quoting a made-up cap would be worse than omitting it.
    const rejection = describeUploadRejection(
      errorFrom(FRAMEWORK_PROBLEM_FIXTURES.payloadTooLarge),
      HUGE,
    );

    expect(rejection.detail).toBe(
      'This file is 212 MB, which is more than this deployment accepts.',
    );
    expect(rejection.retryable).toBe(true);
  });

  it('names the missing grant and offers no retry, which could not supply it', () => {
    const rejection = describeUploadRejection(errorFrom(PROBLEM_FIXTURES.permissionRequired), {
      name: 'notes.txt',
      size: 1024,
    });

    expect(rejection.title).toBe('You can’t upload files');
    expect(rejection.detail).toContain('“Upload File”');
    expect(rejection.detail).toContain('ask an administrator');
    expect(rejection.retryable).toBe(false);
  });

  it('passes the validator’s own words through, which name the actual problem', () => {
    // `userMessage` would say "correct the highlighted fields", pointing at fields
    // this surface does not have.
    const rejection = describeUploadRejection(errorFrom(PROBLEM_FIXTURES.validationError), {
      name: 'notes.txt',
      size: 0,
    });

    expect(rejection.detail).toBe("'Name' must not be empty.");
    expect(rejection.retryable).toBe(false);
  });

  it('says so honestly when the failure carried no trace id', () => {
    const rejection = describeUploadRejection({ kind: 'network', traceId: null } as AppError, HUGE);

    expect(rejection.traceLine).toBe('No trace ID was returned.');
    expect(rejection.retryable).toBe(true);
  });
});
