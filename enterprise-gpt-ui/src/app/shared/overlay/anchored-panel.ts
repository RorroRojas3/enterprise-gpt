import { TABLET_MIN_WIDTH } from '@shared/layout/breakpoints';

/** Where a fixed-position overlay panel sits, in viewport coordinates. */
export interface AnchoredPosition {
  /** Null when the panel is anchored by its bottom edge instead. */
  readonly top: number | null;
  /** Null when the panel is anchored by its top edge instead. */
  readonly bottom: number | null;
  readonly left: number;
  /**
   * The width to write, or null to leave the panel's own CSS width alone. Only the
   * full-width branch below 768px returns a number.
   */
  readonly width: number | null;
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

/** The viewport the panel has to fit inside. */
export interface AnchorViewport {
  readonly width: number;
  readonly height: number;
}

/**
 * The gap kept between a panel and the edge of the viewport, and the inset the
 * full-width branch uses. Frame `1d`'s composer gutter.
 */
const GUTTER = 12;

/**
 * Places a `position: fixed` overlay panel against the element that opened it.
 *
 * Extracted from `Menu` when US-307's project picker needed the same placement without
 * the same semantics: a panel holding a search field cannot be `role="menu"`, but it
 * anchors to its trigger in exactly the way every kebab in this design does.
 *
 * Three details are load-bearing and easy to lose:
 *
 * - **`up` panels anchor by `bottom`, not a measured `top`.** These panels swap content
 *   while open — retry → loading → rows, or a filtered list shrinking as the reader
 *   types — and a top-anchored panel would grow *downward* over the trigger it is
 *   supposed to sit above. Bottom-anchoring makes growth extend upward with no
 *   re-measurement.
 * - **`viewport.height` is the viewport, not `documentElement.clientHeight`.** A
 *   fixed-position `bottom` resolves against the viewport, and the two differ by the
 *   horizontal scrollbar when one exists.
 * - **The horizontal result is clamped, and below 768px it is replaced.** US-1403 added
 *   both. Nothing clamped `left` before, so a 360px model picker opened `align="start"`
 *   from a pill near the right edge simply ran off screen — at every width, not only on
 *   a phone. On a phone clamping is not enough either: a 360px panel does not fit a
 *   390px viewport with anything left over, so it spans the viewport inside a gutter
 *   instead. Neither branch touches `up`/`down`, which is unchanged.
 *
 * @param anchor The bounding rect of the element the panel opens from.
 * @param panelWidth The panel's intended width, for `end` alignment and for deciding
 *   whether it still fits. The *intended* width rather than the measured one: on the
 *   first open at a narrow width the measurement is of a panel not yet resized.
 * @param viewport The viewport's width and height.
 * @param placement How the panel sits relative to the anchor.
 * @returns The `top`/`bottom`/`left`/`width` to write onto the panel.
 */
export function anchoredPosition(
  anchor: DOMRect,
  panelWidth: number,
  viewport: AnchorViewport,
  placement: AnchorPlacement,
): AnchoredPosition {
  const up = placement.direction === 'up';
  const top = up ? null : anchor.bottom + 4;
  const bottom = up ? viewport.height - anchor.top + 4 : null;

  // Below the mobile breakpoint, or whenever the panel simply cannot fit beside its
  // gutters, it spans the viewport. Clamping instead would leave a panel that no longer
  // visually belongs to the control that opened it, and on a 390px viewport it would
  // still overflow.
  const fullWidth = viewport.width < TABLET_MIN_WIDTH || panelWidth + GUTTER * 2 > viewport.width;
  if (fullWidth) {
    return { top, bottom, left: GUTTER, width: viewport.width - GUTTER * 2 };
  }

  const preferred = placement.align === 'end' ? anchor.right - panelWidth : anchor.left;
  const furthest = viewport.width - panelWidth - GUTTER;

  return {
    top,
    bottom,
    left: Math.min(Math.max(preferred, GUTTER), furthest),
    width: null,
  };
}
