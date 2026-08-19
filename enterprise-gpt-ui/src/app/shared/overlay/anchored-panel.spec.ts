import { describe, expect, it } from 'vitest';
import { AnchorPlacement, anchoredPosition } from './anchored-panel';

/**
 * The placement function is pure, so these run with no TestBed, no fixture and no DOM —
 * which is what makes them the right home for US-1403's horizontal rules. jsdom computes
 * no layout, so a rendered panel's position could not be asserted at all.
 */

/** A `DOMRect` is structural here; only the six read properties matter. */
function rect(partial: Partial<DOMRect>): DOMRect {
  return {
    top: 0,
    bottom: 0,
    left: 0,
    right: 0,
    width: 0,
    height: 0,
    x: 0,
    y: 0,
    ...partial,
  } as DOMRect;
}

const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };

const down: AnchorPlacement = { direction: 'down', align: 'start' };
const up: AnchorPlacement = { direction: 'up', align: 'start' };
const downEnd: AnchorPlacement = { direction: 'down', align: 'end' };

describe('anchoredPosition', () => {
  describe('vertical placement', () => {
    it('anchors a downward panel by its top, 4px below the trigger', () => {
      const position = anchoredPosition(rect({ bottom: 120 }), 204, DESKTOP, down);

      expect(position.top).toBe(124);
      expect(position.bottom).toBeNull();
    });

    // A panel that swaps content while open — retry → loading → rows — must grow
    // upward, away from the trigger it sits above. Bottom-anchoring is what does that
    // without a re-measurement, and it is the reason these panels never needed Popper.
    it('anchors an upward panel by its bottom, so it grows away from the trigger', () => {
      const position = anchoredPosition(rect({ top: 700 }), 320, DESKTOP, up);

      expect(position.top).toBeNull();
      expect(position.bottom).toBe(900 - 700 + 4);
    });

    it('is unchanged by the full-width branch', () => {
      const position = anchoredPosition(rect({ top: 700 }), 320, PHONE, up);

      expect(position.top).toBeNull();
      expect(position.bottom).toBe(844 - 700 + 4);
    });
  });

  describe('horizontal placement at a width the panel fits', () => {
    it('lines a start-aligned panel up with the left edge of its trigger', () => {
      const position = anchoredPosition(rect({ left: 300, right: 344 }), 204, DESKTOP, down);

      expect(position.left).toBe(300);
    });

    it('lines an end-aligned panel up with the right edge of its trigger', () => {
      const position = anchoredPosition(rect({ left: 300, right: 344 }), 204, DESKTOP, downEnd);

      expect(position.left).toBe(344 - 204);
    });

    // The defect this closes predates US-1403 and was not narrow-only: nothing clamped
    // `left`, so a 360px picker opened from a pill near the right edge ran off screen at
    // any width.
    it('clamps a panel that would run off the right edge', () => {
      const position = anchoredPosition(rect({ left: 1300, right: 1344 }), 360, DESKTOP, down);

      expect(position.left).toBe(1440 - 360 - 12);
      expect(position.left + 360).toBeLessThanOrEqual(1440 - 12);
    });

    it('clamps a panel that would run off the left edge', () => {
      const position = anchoredPosition(rect({ left: 4, right: 48 }), 204, DESKTOP, downEnd);

      expect(position.left).toBe(12);
    });

    it('leaves the panel its own CSS width', () => {
      expect(anchoredPosition(rect({}), 204, DESKTOP, down).width).toBeNull();
    });
  });

  describe('below the mobile breakpoint', () => {
    it('spans the viewport inside a gutter rather than hanging off the trigger', () => {
      const position = anchoredPosition(rect({ left: 300, right: 344 }), 360, PHONE, down);

      expect(position.left).toBe(12);
      expect(position.width).toBe(390 - 24);
    });

    it('does the same for an end-aligned panel', () => {
      const position = anchoredPosition(rect({ left: 300, right: 344 }), 200, PHONE, downEnd);

      expect(position.left).toBe(12);
      expect(position.width).toBe(366);
    });

    // The tablet band is wide enough for every panel the app has, so it keeps anchoring.
    it('still anchors at 768px, the first width that is not mobile', () => {
      const position = anchoredPosition(
        rect({ left: 100, right: 144 }),
        320,
        { width: 768, height: 900 },
        down,
      );

      expect(position.left).toBe(100);
      expect(position.width).toBeNull();
    });
  });

  // The second half of the full-width condition, and the reason it is not simply a
  // breakpoint check: a panel wider than the viewport cannot be rescued by clamping,
  // whatever the viewport is called.
  it('spans the viewport for a panel too wide to fit its gutters, above the breakpoint', () => {
    const position = anchoredPosition(
      rect({ left: 10, right: 54 }),
      900,
      { width: 900, height: 800 },
      down,
    );

    expect(position.left).toBe(12);
    expect(position.width).toBe(876);
  });
});
