/** Where a fixed-position overlay panel sits, in viewport coordinates. */
export interface AnchoredPosition {
  /** Null when the panel is anchored by its bottom edge instead. */
  readonly top: number | null;
  /** Null when the panel is anchored by its top edge instead. */
  readonly bottom: number | null;
  readonly left: number;
}

/** How an overlay panel is placed relative to the element that opened it. */
export interface AnchorPlacement {
  /**
   * `up` opens above the anchor — the composer sits at the bottom of the screen.
   * Static rather than flipped at the viewport edge, which is exactly why these panels
   * never needed Popper.
   */
  readonly direction: 'down' | 'up';
  /** Which edge of the anchor the panel's matching edge lines up with. */
  readonly align: 'start' | 'end';
}

/**
 * Places a `position: fixed` overlay panel against the element that opened it.
 *
 * Extracted from `Menu` when US-307's project picker needed the same placement without
 * the same semantics: a panel holding a search field cannot be `role="menu"`, but it
 * anchors to its trigger in exactly the way every kebab in this design does.
 *
 * Two details are load-bearing and easy to lose:
 *
 * - **`up` panels anchor by `bottom`, not a measured `top`.** These panels swap content
 *   while open — retry → loading → rows, or a filtered list shrinking as the reader
 *   types — and a top-anchored panel would grow *downward* over the trigger it is
 *   supposed to sit above. Bottom-anchoring makes growth extend upward with no
 *   re-measurement.
 * - **`viewportHeight` is the viewport, not `documentElement.clientHeight`.** A
 *   fixed-position `bottom` resolves against the viewport, and the two differ by the
 *   horizontal scrollbar when one exists.
 *
 * @param anchor The bounding rect of the element the panel opens from.
 * @param panelWidth The panel's measured width, for `end` alignment.
 * @param viewportHeight The viewport height, for an `up` panel's bottom offset.
 * @param placement How the panel sits relative to the anchor.
 * @returns The `top`/`bottom`/`left` to write onto the panel.
 */
export function anchoredPosition(
  anchor: DOMRect,
  panelWidth: number,
  viewportHeight: number,
  placement: AnchorPlacement,
): AnchoredPosition {
  const up = placement.direction === 'up';

  return {
    top: up ? null : anchor.bottom + 4,
    bottom: up ? viewportHeight - anchor.top + 4 : null,
    left: placement.align === 'end' ? anchor.right - panelWidth : anchor.left,
  };
}
