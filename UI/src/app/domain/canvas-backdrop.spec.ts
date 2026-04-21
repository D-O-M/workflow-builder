import { describe, expect, it } from 'vitest';
import {
  canvasPointToWorldPoint,
  getHighlightFalloff,
  getVisibleWorldBounds,
  getWorldRadiusFromScreenRadius,
} from './canvas-backdrop';

describe('canvas-backdrop', () => {
  it('computes visible world bounds from canvas size, viewport offset, and zoom', () => {
    expect(getVisibleWorldBounds(1200, 800, { x: 180, y: -60 }, 2)).toEqual({
      left: -90,
      top: 30,
      right: 510,
      bottom: 430,
    });
  });

  it('converts a screen radius into a zoom-adjusted world radius', () => {
    expect(getWorldRadiusFromScreenRadius(72, 1)).toBe(72);
    expect(getWorldRadiusFromScreenRadius(72, 2)).toBe(36);
    expect(getWorldRadiusFromScreenRadius(72, 0.5)).toBe(144);
  });

  it('converts a canvas point into world space', () => {
    expect(canvasPointToWorldPoint({ x: 320, y: 220 }, { x: 80, y: 20 }, 2)).toEqual({
      x: 120,
      y: 100,
    });
  });

  it('returns highlight falloff values that fade smoothly toward the edge', () => {
    const radiusSquared = 100;

    expect(getHighlightFalloff(0, radiusSquared)).toBe(1);
    expect(getHighlightFalloff(25, radiusSquared)).toBeCloseTo(0.5625);
    expect(getHighlightFalloff(100, radiusSquared)).toBe(0);
    expect(getHighlightFalloff(121, radiusSquared)).toBe(0);
  });
});
