/**
 * Minimal `<dialog>` for jsdom, which implements only `open`. It never restores focus, so specs
 * test the component's focus return; top layer, backdrop and inerting are checked in `/ui-kit`.
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
    // Queued as on the platform: an inline event would let `Modal`'s destroy-time focus restore
    // be deleted with every spec still green.
    queueMicrotask(() => this.dispatchEvent(new Event('close')));
  };
}
