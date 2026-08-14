import { Provider } from '@angular/core';
import { UPLOAD_XHR } from '../app/core/documents/upload-xhr.token';

/**
 * A stand-in for one `XMLHttpRequest`.
 *
 * jsdom ships an `XMLHttpRequest` that would attempt a real request, and — more to the
 * point — offers no way to drive `upload.onprogress`, which is the whole reason the
 * upload transport is on XHR rather than `HttpClient` in the first place. Everything
 * the transport touches is implemented here and nothing else is.
 */
export class FakeUploadXhr {
  status = 0;
  response: unknown = null;
  responseType: XMLHttpRequestResponseType = '';

  readonly headers = new Map<string, string>();
  method: string | null = null;
  url: string | null = null;
  body: FormData | null = null;
  aborted = false;

  readonly upload = {
    addEventListener: (type: string, listener: (event: ProgressEvent) => void) => {
      if (type === 'progress') {
        this._onProgress.push(listener);
      }
    },
  };

  private readonly _onProgress: ((event: ProgressEvent) => void)[] = [];
  private readonly _listeners = new Map<string, (() => void)[]>();

  open(method: string, url: string): void {
    this.method = method;
    this.url = url;
  }

  setRequestHeader(name: string, value: string): void {
    this.headers.set(name, value);
  }

  addEventListener(type: string, listener: () => void): void {
    this._listeners.set(type, [...(this._listeners.get(type) ?? []), listener]);
  }

  send(body: FormData): void {
    this.body = body;
  }

  abort(): void {
    this.aborted = true;
    this.emit('abort');
  }

  /** Drives `upload.onprogress`, the event only the XHR backend produces. */
  progress(loaded: number, total: number): void {
    for (const listener of this._onProgress) {
      listener({ lengthComputable: true, loaded, total } as ProgressEvent);
    }
  }

  /** Completes the exchange with a status and a parsed body. */
  respond(status: number, response: unknown = null): void {
    this.status = status;
    this.response = response;
    this.emit('load');
  }

  /** A transport failure — no response ever arrives. */
  fail(): void {
    this.emit('error');
  }

  /** The file the transport appended, so a spec can assert the field name. */
  file(): File | null {
    const value = this.body?.get('file');

    return value instanceof File ? value : null;
  }

  private emit(type: string): void {
    for (const listener of this._listeners.get(type) ?? []) {
      listener();
    }
  }
}

/** A queue of {@link FakeUploadXhr}s, one per request the transport issues. */
export class FakeUploadXhrQueue {
  readonly requests: FakeUploadXhr[] = [];

  /** `[method, url]` per request, in issue order. */
  get opened(): [string, string][] {
    return this.requests.map((request) => [request.method ?? '', request.url ?? '']);
  }

  /** The most recent request, which is the one a spec almost always means. */
  get last(): FakeUploadXhr {
    const request = this.requests.at(-1);
    if (request === undefined) {
      throw new Error('No upload request has been issued.');
    }

    return request;
  }

  create(): XMLHttpRequest {
    const request = new FakeUploadXhr();
    this.requests.push(request);

    return request as unknown as XMLHttpRequest;
  }
}

/**
 * Provides a fake XHR factory for the upload transport, and hands back the queue.
 *
 * @example
 * const uploads = new FakeUploadXhrQueue();
 * TestBed.configureTestingModule({ providers: [provideFakeUploadXhr(uploads)] });
 */
export function provideFakeUploadXhr(queue: FakeUploadXhrQueue): Provider {
  return { provide: UPLOAD_XHR, useValue: () => queue.create() };
}
