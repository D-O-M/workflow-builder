import { describe, expect, it } from 'vitest';
import { bridgePointFromDividerRect, createPhaseBridgeCurvePath, normalizeBridgePosition } from './phase-bridge-geometry';

describe('phase bridge geometry', () => {
  it('normalizes bridge positions into the divider range', () => {
    expect(normalizeBridgePosition(null)).toBe(0.5);
    expect(normalizeBridgePosition(-0.25)).toBe(0);
    expect(normalizeBridgePosition(1.25)).toBe(1);
    expect(normalizeBridgePosition(0.4)).toBe(0.4);
  });

  it('resolves divider points for horizontal and vertical splits', () => {
    const rect = { left: 100, right: 140, top: 200, bottom: 260, width: 40, height: 60 };

    expect(bridgePointFromDividerRect('input', 0.25, 'horizontal', rect)).toEqual({ clientX: 110, clientY: 200 });
    expect(bridgePointFromDividerRect('output', 0.25, 'horizontal', rect)).toEqual({ clientX: 110, clientY: 260 });
    expect(bridgePointFromDividerRect('input', 0.5, 'vertical', rect)).toEqual({ clientX: 100, clientY: 230 });
    expect(bridgePointFromDividerRect('output', 0.5, 'vertical', rect)).toEqual({ clientX: 140, clientY: 230 });
  });

  it('adds curved outward protrusion control points before bridge curves travel inward', () => {
    expect(createPhaseBridgeCurvePath(
      { left: 0, top: 0 },
      { clientX: 0, clientY: 0 },
      { clientX: 100, clientY: 80 },
      { startOutward: 'right', endOutward: 'left' },
    )).toBe('M 0 0 C 34 0 66 80 100 80');
  });
});
