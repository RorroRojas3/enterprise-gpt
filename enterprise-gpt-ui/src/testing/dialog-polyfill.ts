/**
 * Minimal `<dialog>` support for jsdom, which implements only `open` — no
 * `showModal`, no `show`, no `close`, and no `cancel`/`close` events.
 *
 * Deliberately does **not** restore focus on close. Focus return is the component's
 * job and an acceptance criterion; implementing it here would make the assertion test
 * the shim instead of the code.
 *
 * Also absent, and unverifiable under jsdom as a result: the top layer, `::backdrop`,
 * light dismiss, and native inerting of the page behind the dialog. Those are checked
 * in a browser through the `/ui-kit` gallery.
 */
export function installDialogPolyfill(): void {
  const proto = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
  if (typeof proto['showModal'] === 'function') {
    return;
  }

  proto['showModal'] = function (this: HTMLDialogElement): void {
    this.setAttribute('open', '');
  };

  proto['show'] = function (this: HTMLDialogElement): void {
    this.setAttribute('open', '');
  };

  proto['close'] = function (this: HTMLDialogElement, returnValue?: string): void {
    if (!this.hasAttribute('open')) {
      return;
    }
    this.removeAttribute('open');
    if (returnValue !== undefined) {
      this.returnValue = returnValue;
    }
    // **Queued, not synchronous**, because the platform queues it — and because a
    // synchronous shim quietly makes `Modal`'s destroy-time focus restore look
    // redundant. That restore exists precisely for the case where the `(close)`
    // binding is torn down before the queued event fires, so a shim that fires it
    // inline lets the guard be deleted with every spec still green.
    queueMicrotask(() => this.dispatchEvent(new Event('close')));
  };
}
