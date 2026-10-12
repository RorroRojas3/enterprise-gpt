import { TABLET_MIN_WIDTH } from '@shared/utils/layout/breakpoints';

/** Where a fixed-position overlay panel sits, in viewport coordinates. */
export interface AnchoredPosition {
  /** Null when the panel is anchored by its bottom edge instead. */
  readonly top: number | null;
  /** Null when the panel is anchored by its top edge instead. */
  readonly bottom: number | null;
  readonly left: number;
  /** Null leaves the panel's own CSS width alone; only the full-width branch writes one. */
  readonly width: number | null;
}

/** How an overlay panel is placed relative to the element that opened it. */
export interface AnchorPlacement {
  /**
   * `up` opens above the anchor, for the composer. Static rather than flipped at the viewport
   * edge, which is why these panels never needed Popper.
   */
  readonly direction: 'down' | 'up';
  /** Which edge of the anchor the panel's matching edge lines up with. */
  readonly align: 'start' | 'end';
}

export interface AnchorViewport {
  readonly width: number;
  readonly height: number;
}

/** The gap kept between a panel and the viewport edge, and the full-width branch's inset. */
const GUTTER = 12;

/**
 * Shared by `Menu` and panels that cannot be `role="menu"`. `up` panels anchor by `bottom`, so
 * content swaps grow upward without re-measuring. `viewport.height` must be the viewport a fixed
 * `bottom` resolves against, not `clientHeight`, which excludes a horizontal scrollbar. `left` is
 * clamped, and below 768px the panel spans the viewport inside a gutter.
 *
 * @param panelWidth The intended width, not the measured one, which on a first narrow open is of
 *   a panel not yet resized.
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

  // Spans the viewport when mobile or too wide for its gutters: a clamped panel would no
  // longer belong to its control, and on a 390px viewport would still overflow.
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
