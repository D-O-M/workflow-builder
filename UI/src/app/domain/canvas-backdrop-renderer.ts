import {
    CanvasBackgroundEffect,
    canvasPointToWorldPoint,
    getHighlightFalloff,
    getVisibleWorldBounds,
    getWorldRadiusFromScreenRadius,
} from './canvas-backdrop';
import { Canvas2DContext, CanvasRenderSurface } from './canvas-draw-surface';
import { CanvasTheme } from './canvas-theme';
import { Point } from './node-model';

export interface CanvasBackdropRenderRequest {
    readonly canvasWidth: number;
    readonly canvasHeight: number;
    readonly effect: CanvasBackgroundEffect;
    readonly hoveredCanvasPoint: Point | null;
    readonly viewportOffset: Point;
    readonly zoomLevel: number;
}

export class CanvasBackdropRenderer {
    private stitchPattern: CanvasPattern | null = null;

    constructor(private readonly surface: CanvasRenderSurface) {}

    drawBackdrop(request: CanvasBackdropRenderRequest): void {
        if (request.effect !== 'stitch') {
            return;
        }

        const visibleBounds = getVisibleWorldBounds(
            request.canvasWidth,
            request.canvasHeight,
            request.viewportOffset,
            request.zoomLevel,
        );
        const pattern = this.getStitchPattern();
        if (pattern) {
            this.surface.ctx.save();
            this.surface.ctx.fillStyle = pattern;
            this.surface.ctx.fillRect(
                visibleBounds.left,
                visibleBounds.top,
                visibleBounds.right - visibleBounds.left,
                visibleBounds.bottom - visibleBounds.top,
            );
            this.surface.ctx.restore();
        }

        if (!request.hoveredCanvasPoint) {
            return;
        }

        const hoverCenter = canvasPointToWorldPoint(
            request.hoveredCanvasPoint,
            request.viewportOffset,
            request.zoomLevel,
        );
        const radiusWorld = getWorldRadiusFromScreenRadius(
            CanvasTheme.backdrop.hoverRadiusPx,
            request.zoomLevel,
        );
        const radiusSquared = radiusWorld * radiusWorld;
        const startX = this.snapBackdropCoordinate(hoverCenter.x - radiusWorld);
        const endX = this.snapBackdropCoordinate(hoverCenter.x + radiusWorld);
        const startY = this.snapBackdropCoordinate(hoverCenter.y - radiusWorld);
        const endY = this.snapBackdropCoordinate(hoverCenter.y + radiusWorld);

        this.surface.ctx.save();
        for (let x = startX; x <= endX; x += CanvasTheme.backdrop.dotSpacing) {
            for (
                let y = startY;
                y <= endY;
                y += CanvasTheme.backdrop.dotSpacing
            ) {
                const deltaX = x - hoverCenter.x;
                const deltaY = y - hoverCenter.y;
                const distanceSquared = deltaX * deltaX + deltaY * deltaY;
                const strength = getHighlightFalloff(
                    distanceSquared,
                    radiusSquared,
                );
                if (strength <= 0) {
                    continue;
                }

                this.surface.ctx.globalAlpha =
                    CanvasTheme.backdrop.hoverMaxAlpha * strength;
                this.surface.ctx.fillStyle = `rgb(${CanvasTheme.backdrop.hoverColorRgb})`;
                this.surface.ctx.beginPath();
                this.surface.ctx.arc(
                    x,
                    y,
                    CanvasTheme.backdrop.dotRadius +
                        CanvasTheme.backdrop.hoverDotRadiusBoost * strength,
                    0,
                    Math.PI * 2,
                );
                this.surface.ctx.fill();
            }
        }
        this.surface.ctx.restore();
    }

    private getStitchPattern(): CanvasPattern | null {
        if (this.stitchPattern) {
            return this.stitchPattern;
        }

        const size = CanvasTheme.backdrop.dotSpacing;
        const tile = createPatternTile(size);
        if (!tile) {
            return null;
        }

        const tileContext = tile.getContext('2d') as Canvas2DContext | null;
        if (!tileContext) {
            return null;
        }

        tileContext.fillStyle = CanvasTheme.backdrop.baseDotColor;
        tileContext.beginPath();
        tileContext.arc(
            CanvasTheme.backdrop.dotOffset,
            CanvasTheme.backdrop.dotOffset,
            CanvasTheme.backdrop.dotRadius,
            0,
            Math.PI * 2,
        );
        tileContext.fill();

        this.stitchPattern = this.surface.ctx.createPattern(tile, 'repeat');
        return this.stitchPattern;
    }

    private snapBackdropCoordinate(value: number): number {
        const { dotSpacing, dotOffset } = CanvasTheme.backdrop;
        return (
            Math.floor((value - dotOffset) / dotSpacing) * dotSpacing +
            dotOffset
        );
    }
}

function createPatternTile(
    size: number,
): HTMLCanvasElement | OffscreenCanvas | null {
    if (typeof OffscreenCanvas === 'function') {
        return new OffscreenCanvas(size, size);
    }

    if (typeof document === 'undefined') {
        return null;
    }

    const tile = document.createElement('canvas');
    tile.width = size;
    tile.height = size;
    return tile;
}
